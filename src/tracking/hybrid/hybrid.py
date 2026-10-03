"""Run the verified Quest Pro / Virtual Desktop / SteamVR hybrid tracking setup.

QFT+ stops it by closing stdin. Hooks renew a short lease so they expire
if this controller stops responding. No installed APK or DLL is patched.
"""
import argparse
import ctypes as C
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import threading
import time

HERE = Path(__file__).resolve().parent
DRIVER = Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / 'Virtual Desktop Streamer/OpenVRDriver/bin/win64/driver_VirtualDesktop.dll'
DRIVER_HASH = 'ad3c99c7f7346613d4c7106fb94476be5859ca17a637a11cfc156184d466f36f'
FRIDA_VERSION = '17.18.0'
SERVER = 'quest-hybrid-frida'
LEASE_WAIT = 35
try:
    import frida
except ImportError:
    frida = None
from headset import RootShell, adb, start_frida_server, stop_event, stop_frida_server


class HookCompatibilityError(RuntimeError):
    pass


class HookCleanupError(RuntimeError):
    pass


def load_hand_hook(session, receive, filename='vd_hand_hook.js'):
    errors = []
    script = session.create_script((HERE / filename).read_text())
    def startup_message(event, data):
        if event['type'] == 'error':
            errors.append(event.get('description', str(event)))
        else:
            receive(event, data)
    script.on('message', startup_message)
    try:
        script.load()
        script.exports_sync.status()
        if errors:
            raise RuntimeError(errors[-1])
    except Exception as exc:
        try:
            script.unload()
        except Exception:
            pass
        if errors and 'Unsupported controller routing code' in errors[-1]:
            raise HookCompatibilityError(errors[-1]) from exc
        raise RuntimeError(errors[-1] if errors else 'Headset hook did not initialize') from exc
    script.off('message', startup_message)
    script.on('message', receive)
    return script


def wait_for_hand_frames(quest, keep_alive, timeout=30):
    """Installing a hook does not prove VD has entered its update loop."""
    end = time.monotonic() + timeout
    while True:
        keep_alive()
        state = quest.exports_sync.status()
        if state['state'] == 'running' and state.get('frames', 0) > 0:
            return
        if state['state'] not in ('starting', 'applying', 'running'):
            raise RuntimeError(state.get('reason') or 'Headset hook stopped before tracking began')
        if time.monotonic() >= end:
            raise RuntimeError('No headset tracking frames received. Wear the headset and connect Virtual Desktop.')
        time.sleep(0.25)


def attach_steamvr(keep_alive):
    device = frida.get_local_device()
    waiting = False
    while True:
        keep_alive()
        try:
            return device.attach('vrserver.exe')
        except frida.ProcessNotFoundError:
            if not waiting:
                print('Waiting for SteamVR before starting hybrid tracking.', flush=True)
                waiting = True
            time.sleep(1)


def wait_for_virtual_desktop(adb, keep_alive):
    """With hand tracking only, QFT+ starts before the headset is in Virtual Desktop."""
    waiting = False
    while True:
        keep_alive()
        pid = adb('shell', 'pidof VirtualDesktop.Android || true').split()
        if pid:
            return int(pid[0])
        if not waiting:
            print('Waiting for Virtual Desktop in the headset.', flush=True)
            waiting = True
        time.sleep(1)


def stop_scripts(scripts, detached, keep_alive):
    """A session that ended on its own (process exit, dropped connection) has nothing left to stop:
    Frida awaits each script's dispose(), which restores its changes, and if the headset hasn't
    noticed the drop yet the lease does the same (see LEASE_WAIT)."""
    errors = []
    for source, script in reversed(scripts):
        keep_alive()
        session_source = 'Quest' if source == 'Quest controllers' else source
        if session_source in detached:
            print(source + ' hooks were unloaded with the session (' + detached[session_source] + ').', flush=True)
            continue
        try:
            script.exports_sync.stop()
            if source == 'Quest':
                for _ in range(80):
                    keep_alive()
                    state = script.exports_sync.status()
                    if state['state'] in ('idle', 'stopped'):
                        break
                    if state['state'] == 'restore-failed':
                        raise RuntimeError(state.get('cleanupError') or 'Headset restoration failed')
                    time.sleep(0.1)
                else:
                    raise RuntimeError('Headset restoration was not acknowledged. Restart Virtual Desktop on the headset.')
        except Exception as exc:
            end = time.monotonic() + 2
            while session_source not in detached and time.monotonic() < end:
                keep_alive()
                time.sleep(0.05)
            if session_source not in detached:
                errors.append(source + ': ' + str(exc))
    return errors


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--target', required=True)
    args = ap.parse_args()
    if not re.fullmatch(r'[A-Za-z0-9_.:-]+', args.target):
        ap.error('Invalid ADB target')
    os.environ['ANDROID_SERIAL'] = args.target
    if frida is None or frida.__version__ != FRIDA_VERSION:
        raise HookCompatibilityError('Hybrid components are missing or outdated. Run setup again.')
    if not DRIVER.is_file() or hashlib.sha256(DRIVER.read_bytes()).hexdigest() != DRIVER_HASH:
        raise HookCompatibilityError("This PC's Virtual Desktop Streamer driver is missing or a version hybrid hands "
                                     "doesn't support yet. No hook was applied.")
    kernel = C.WinDLL('kernel32', use_last_error=True)
    kernel.CreateMutexW.argtypes = [C.c_void_p, C.c_bool, C.c_wchar_p]
    kernel.CreateMutexW.restype = C.c_void_p
    kernel.CloseHandle.argtypes = [C.c_void_p]
    mutex = kernel.CreateMutexW(None, False, 'Local\\QuestProVirtualDesktopHybrid')
    if not mutex:
        raise C.WinError(C.get_last_error())
    if C.get_last_error() == 183:
        kernel.CloseHandle(mutex)
        raise RuntimeError('Hybrid tracking is already running.')

    sessions, scripts, errors = [], [], []
    detached = {}
    priority = None
    quest = None
    root = None
    forward_port = None
    stopped = stop_event()
    progress = [None]
    def beat():
        progress[0] = time.monotonic()
    def keep_alive():
        beat()
        if stopped.is_set():
            raise KeyboardInterrupt
        if detached:
            source, reason = next(iter(detached.items()))
            raise RuntimeError(source + ' disconnected: ' + reason)
        if errors:
            raise RuntimeError(errors[-1])
    def track_session(source, session):
        def on_detached(reason, crash):
            detached[source] = reason
            print(source + ' session ended: ' + reason + (' (' + crash.summary + ')' if crash else ''), flush=True)
        session.on('detached', on_detached)
        sessions.append((source, session))
        return session
    def watchdog():
        while True:
            time.sleep(1)
            if progress[0] is not None and time.monotonic() - progress[0] > 20:
                print('Headset stopped responding (Virtual Desktop closed, headset asleep or Wi-Fi dropped).', flush=True)
                stopped.wait(LEASE_WAIT)
                os._exit(1)
    threading.Thread(target=watchdog, daemon=True).start()

    def message(source):
        def receive(event, data):
            if event['type'] == 'error':
                errors.append(source + ': ' + event.get('description', str(event)))
                print('ERROR:', errors[-1], flush=True)
                return
            payload = event.get('payload', {})
            if payload.get('event') in ('applied', 'apply-failed', 'restore-failed', 'stopped', 'resolved', 'route', 'restored', 'controller-changed', 'controller-runtime'):
                print(time.strftime('%H:%M:%S.') + '%03d' % (time.time() * 1000 % 1000), source, json.dumps(payload), flush=True)
        return receive

    try:
        if adb('get-state') != 'device':
            raise RuntimeError('Headset is not connected through ADB')
        package = adb('shell', 'dumpsys', 'package', 'VirtualDesktop.Android')
        if not re.search(r'versionName=1\.34\.22\.0\s', package):
            raise RuntimeError('This hook was verified with Virtual Desktop Android 1.34.22.0 only')
        root = RootShell()
        print('Headset build ' + root.run('getprop ro.build.version.incremental'), flush=True)
        wait_for_virtual_desktop(adb, keep_alive)
        pc_session = track_session('PC', attach_steamvr(keep_alive))
        progress[0] = None
        device, forward_port = start_frida_server(root, SERVER, 27042, reuse=True)

        pid = int(adb('shell', 'pidof', 'VirtualDesktop.Android'))
        try:
            quest_session = device.attach(pid)
        except (frida.ProcessNotFoundError, frida.ProcessNotRespondingError):
            current_pid = int(adb('shell', 'pidof', 'VirtualDesktop.Android'))
            if current_pid == pid:
                raise
            quest_session = device.attach(current_pid)
        track_session('Quest', quest_session)
        priority = load_hand_hook(quest_session, message('Controllers'), 'vd_controller_priority.js')
        scripts.append(('Quest controllers', priority))
        quest = load_hand_hook(quest_session, message('Quest'))
        scripts.append(('Quest', quest))
        pc = pc_session.create_script((HERE / 'vd_pc_skeleton.js').read_text())
        pc.on('message', message('PC'))
        scripts.append(('PC', pc))
        pc.load()
        waiting = False
        while True:
            keep_alive()
            if pc.exports_sync.start(30):
                break
            if not waiting:
                print('Waiting for the Virtual Desktop SteamVR driver.', flush=True)
                waiting = True
            time.sleep(1)
        priority.exports_sync.start(30)
        quest.exports_sync.apply(30)
        def renew(when):
            """Every hook must still be active; then all three leases are extended. Returns the PC and controller status."""
            keep_alive()
            state = quest.exports_sync.status()
            if state['state'] not in ('starting', 'applying', 'running'):
                raise RuntimeError(state.get('reason') or 'Headset hook stopped ' + when)
            pc_state = pc.exports_sync.status()
            if not pc_state['running']:
                raise RuntimeError(pc_state.get('reason') or 'PC routing stopped ' + when)
            selection = priority.exports_sync.status()
            if not selection['running']:
                raise RuntimeError('Controller selection stopped ' + when)
            for script in (priority, quest, pc):
                script.exports_sync.renew(30)
            return pc_state, selection
        wait_for_hand_frames(quest, lambda: renew('before tracking began'))
        print('Hybrid tracking is running.', flush=True)
        print('QROOT_READY', flush=True)
        next_renew = next_report = 0
        while not stopped.is_set():
            keep_alive()
            if time.monotonic() >= next_renew:
                pc_state, selection = renew('unexpectedly')
                if time.monotonic() >= next_report:
                    print(time.strftime('%H:%M:%S.') + '%03d' % (time.time() * 1000 % 1000), 'PC',
                        json.dumps({'event': 'routing-status', **pc_state}), flush=True)
                    print(time.strftime('%H:%M:%S.') + '%03d' % (time.time() * 1000 % 1000), 'Controllers',
                        json.dumps({'event': 'selection', 'queries': selection['queries'], 'selected': selection['selected']}), flush=True)
                    next_report = time.monotonic() + 10
                next_renew = time.monotonic() + 5
            time.sleep(0.25)
    except KeyboardInterrupt:
        print('Stopping...', flush=True)
    finally:
        cleanup_errors = stop_scripts(scripts, detached, beat)
        ended = dict(detached)
        for source, session in reversed(sessions):
            beat()
            try:
                session.detach()
            except Exception as exc:
                if source not in ended:
                    cleanup_errors.append(source + ' detach: ' + str(exc))
        progress[0] = None
        if forward_port is not None:
            try:
                stop_frida_server(root, SERVER)
            except Exception as exc:
                if 'Quest' in ended:
                    print('Headset helper not stopped yet (headset unreachable); the headset stops it once the connection closes.', flush=True)
                else:
                    cleanup_errors.append('Server cleanup: ' + str(exc))
            try:
                adb('forward', '--remove', f'tcp:{forward_port}')
            except Exception:
                pass
        if root is not None:
            root.close()
        kernel.CloseHandle(mutex)
        if cleanup_errors:
            if sys.exception() is not None:
                print('Tracking failed before cleanup: ' + str(sys.exception()), flush=True)
            print('Cleanup needs attention: ' + '; '.join(cleanup_errors), flush=True)
            raise HookCleanupError('Runtime restoration was not verified. Restart Virtual Desktop on the headset and SteamVR before starting hybrid tracking again.')
        else:
            print('Stopped; cleanup completed.', flush=True)
            if 'Quest' in ended:
                print('Lost the headset; retrying in ' + str(LEASE_WAIT) + ' s.', flush=True)
                stopped.wait(LEASE_WAIT)


if __name__ == '__main__':
    try:
        main()
    except HookCompatibilityError as error:
        print('QROOT_INCOMPATIBLE ' + str(error), flush=True)
        sys.exit(4)
    except HookCleanupError as error:
        print('QROOT_CLEANUP_FAILED ' + str(error), flush=True)
        sys.exit(5)
