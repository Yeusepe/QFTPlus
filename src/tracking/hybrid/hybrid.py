"""Run the verified Quest Pro / Virtual Desktop / SteamVR hybrid tracking setup.

Stop with Ctrl+C or Stop Hybrid.cmd. Hooks renew a short lease so they expire
if this controller stops responding. No installed APK or DLL is patched.
"""
import argparse
import ctypes as C
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import threading
import time

HERE = Path(__file__).resolve().parent
CONTEXT = json.loads(Path(os.environ['QROOT_CONTEXT']).read_text(encoding='utf-8-sig')) if os.environ.get('QROOT_CONTEXT') else {}
ADB = Path(CONTEXT.get('adb', os.environ.get('QPRO_ADB', str(HERE.parent / 'platform-tools/adb.exe'))))
STOP = Path(CONTEXT.get('stopFile', str(HERE / 'hybrid.stop')))
DRIVER = Path(CONTEXT.get('settings', {}).get('driverPath') or str(Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / 'Virtual Desktop Streamer/OpenVRDriver/bin/win64/driver_VirtualDesktop.dll'))
DRIVER_HASH = 'ad3c99c7f7346613d4c7106fb94476be5859ca17a637a11cfc156184d466f36f'
SERVER = '/data/local/tmp/quest-hybrid-frida'
import frida
from qroot_owner import owner_alive


def load_hand_hook(session, receive):
    errors = []
    script = session.create_script((HERE / 'vd_hand_hook.js').read_text())
    def startup_message(event, data):
        if event['type'] == 'error':
            errors.append(event.get('description', str(event)))
    script.on('message', startup_message)
    try:
        script.load()
        script.exports_sync.status()
        if errors:
            raise RuntimeError(errors[-1])
    except Exception as exc:
        script.unload()
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
        if state['state'] not in ('starting', 'running'):
            raise RuntimeError('Headset hook stopped before tracking began')
        if time.monotonic() >= end:
            raise RuntimeError('No headset tracking frames received. Wear the headset and connect Virtual Desktop.')
        time.sleep(0.25)


def attach_steamvr(keep_alive):
    device = frida.get_local_device()
    waiting = False
    while True:
        keep_alive()
        try:
            return device.attach('vrserver.exe', persist_timeout=60)
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
    errors = []
    for source, script in reversed(scripts):
        keep_alive()
        session_source = 'Quest' if source == 'Quest controllers' else source
        if detached.get(session_source) == 'process-terminated':
            print(source + ' process exited; its tracking hooks are gone.', flush=True)
            continue
        try:
            script.exports_sync.stop()
            if source == 'Quest':
                for _ in range(80):
                    keep_alive()
                    if script.exports_sync.status()['state'] in ('idle', 'stopped'):
                        break
                    time.sleep(0.1)
                else:
                    raise RuntimeError('Headset restoration was not acknowledged. Restart Virtual Desktop on the headset.')
        except Exception as exc:
            if detached.get(session_source) != 'process-terminated':
                errors.append(source + ': ' + str(exc))
    return errors


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--target', required=True)
    ap.add_argument('--check', action='store_true')
    ap.add_argument('--seconds', type=int, default=43200, help='Maximum session length (default 12 hours)')
    ap.add_argument('--stop', action='store_true')
    args = ap.parse_args()
    if args.check:
        if not DRIVER.is_file() or hashlib.sha256(DRIVER.read_bytes()).hexdigest() != DRIVER_HASH:
            raise RuntimeError('Unsupported Virtual Desktop PC driver. Select the verified driver path; no hook was applied.')
        print('Hybrid runtime and driver compatibility verified.')
        return
    if args.stop:
        STOP.touch()
        print('Stop requested. Original settings will be restored within a few seconds.')
        return
    if not 1 <= args.seconds <= 43200:
        ap.error('Use 1..43200 seconds')
    if not re.fullmatch(r'[A-Za-z0-9_.:-]+', args.target):
        ap.error('Invalid ADB target')
    kernel = C.WinDLL('kernel32', use_last_error=True)
    kernel.CreateMutexW.argtypes = [C.c_void_p, C.c_bool, C.c_wchar_p]
    kernel.CreateMutexW.restype = C.c_void_p
    kernel.CloseHandle.argtypes = [C.c_void_p]
    mutex = kernel.CreateMutexW(None, False, 'Local\\QuestProVirtualDesktopHybrid')
    if not mutex:
        raise C.WinError(C.get_last_error())
    if C.get_last_error() == 183:
        kernel.CloseHandle(mutex)
        raise RuntimeError('Hybrid tracking is already running. Use Stop Hybrid.cmd first.')

    sessions, scripts, errors = [], [], []
    detached = {}
    failed = False
    starter = None
    priority = None
    quest = None
    owned_server = False
    forwarded = False
    STOP.unlink(missing_ok=True)
    progress = [None]
    def beat():
        progress[0] = time.monotonic()
    def keep_alive():
        beat()
        if STOP.exists() or not owner_alive(CONTEXT.get('ownerPid')):
            raise KeyboardInterrupt
        if detached:
            source, reason = next(iter(detached.items()))
            raise RuntimeError(source + ' disconnected: ' + reason)
        if errors:
            raise RuntimeError(errors[-1])
    def track_session(source, session):
        session.on('detached', lambda reason, crash: detached.__setitem__(source, reason))
        sessions.append(session)
        return session
    def watchdog():
        while True:
            time.sleep(1)
            if progress[0] is not None and time.monotonic() - progress[0] > 20:
                print('Headset stopped responding (Virtual Desktop closed, headset asleep or Wi-Fi dropped).', flush=True)
                os._exit(3)
    threading.Thread(target=watchdog, daemon=True).start()

    def adb(*commands):
        return subprocess.check_output([str(ADB), '-s', args.target, *commands],
            text=True, stderr=subprocess.STDOUT, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW).strip()

    def root(command):
        return adb('shell', 'su', '-c', command)

    def message(source):
        def receive(event, data):
            if event['type'] == 'error':
                errors.append(source + ': ' + event.get('description', str(event)))
                print('ERROR:', errors[-1], flush=True)
                return
            payload = event.get('payload', {})
            if payload.get('event') in ('applied', 'stopped', 'route', 'restored', 'controller-changed'):
                print(source, json.dumps(payload), flush=True)
        return receive

    try:
        if hashlib.sha256(DRIVER.read_bytes()).hexdigest() != DRIVER_HASH:
            raise RuntimeError('Virtual Desktop PC driver changed; this version needs a new offset review.')
        if adb('get-state') != 'device':
            raise RuntimeError('Headset is not connected through ADB')
        package = adb('shell', 'dumpsys', 'package', 'VirtualDesktop.Android')
        if not re.search(r'versionName=1\.34\.22\.0\s', package):
            raise RuntimeError('This hook was verified with Virtual Desktop Android 1.34.22.0 only')
        if root('getprop ro.build.version.incremental') != '51503870024400340':
            raise RuntimeError('Headset firmware changed; revalidate before applying')
        wait_for_virtual_desktop(adb, keep_alive)
        pc_session = track_session('PC', attach_steamvr(keep_alive))
        progress[0] = None

        existing = adb('forward', '--list').splitlines()
        for line in existing:
            parts = line.split()
            if len(parts) == 3 and parts[1] == 'tcp:27043' and parts != [args.target, 'tcp:27043', 'tcp:27042']:
                raise RuntimeError('Local ADB port 27043 is already used by another forwarding rule')
        local_server = HERE / 'frida-server-17.18.0-android-arm64'
        server_hash = hashlib.sha256(local_server.read_bytes()).hexdigest()
        remote_hash = root('sha256sum ' + SERVER + ' 2>/dev/null || true').split()
        server_pids = root('pidof quest-hybrid-frida || true')
        if server_pids and (not remote_hash or remote_hash[0] != server_hash):
            raise RuntimeError('The running headset helper differs from the bundled version. Restart the headset before retrying.')
        if not server_pids:
            if not remote_hash or remote_hash[0] != server_hash:
                adb('push', str(HERE / 'frida-server-17.18.0-android-arm64'), SERVER)
                adb('shell', 'chmod', '755', SERVER)
                if root('sha256sum ' + SERVER).split()[0] != server_hash:
                    raise RuntimeError('The headset helper failed verification')
            owned_server = True
            starter = subprocess.Popen([str(ADB), '-s', args.target, 'shell', 'su', '-c',
                SERVER + ' --listen 127.0.0.1:27042 --disable-preload --daemonize'],
                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW)
            for _ in range(20):
                if root('pidof quest-hybrid-frida || true'):
                    break
                time.sleep(0.25)
            else:
                raise RuntimeError('Root instrumentation helper did not start')
        forwarded = not any(line.split() == [args.target, 'tcp:27043', 'tcp:27042'] for line in existing)
        if forwarded:
            adb('forward', 'tcp:27043', 'tcp:27042')
        device = frida.get_device_manager().add_remote_device('127.0.0.1:27043')
        for attempt in range(20):
            try:
                device.enumerate_processes()
                break
            except frida.TransportError:
                if attempt == 19:
                    raise
                time.sleep(0.25)

        pid = int(adb('shell', 'pidof', 'VirtualDesktop.Android'))
        try:
            quest_session = device.attach(pid, persist_timeout=60)
        except (frida.ProcessNotFoundError, frida.ProcessNotRespondingError):
            current_pid = int(adb('shell', 'pidof', 'VirtualDesktop.Android'))
            if current_pid == pid:
                raise
            quest_session = device.attach(current_pid, persist_timeout=60)
        track_session('Quest', quest_session)
        priority = quest_session.create_script((HERE / 'vd_controller_priority.js').read_text())
        priority.on('message', message('Controllers'))
        scripts.append(('Quest controllers', priority))
        priority.load()
        priority.exports_sync.status()
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
        def renew_startup():
            keep_alive()
            pc_state = pc.exports_sync.status()
            if not pc_state['running']:
                raise RuntimeError(pc_state.get('reason') or 'PC routing stopped before tracking began')
            if not priority.exports_sync.status()['running']:
                raise RuntimeError('Controller selection stopped before tracking began')
            priority.exports_sync.renew(30)
            quest.exports_sync.renew(30)
            pc.exports_sync.renew(30)
        wait_for_hand_frames(quest, renew_startup)
        if starter is not None:
            if starter.poll() is None:
                starter.terminate()
            starter.wait(timeout=5)
            starter = None
        print('Hybrid tracking is running.', flush=True)
        print('QROOT_READY', flush=True)
        end = time.monotonic() + args.seconds
        next_renew = 0
        while time.monotonic() < end and not STOP.exists() and owner_alive(CONTEXT.get('ownerPid')):
            keep_alive()
            if time.monotonic() >= next_renew:
                if quest.exports_sync.status()['state'] not in ('starting', 'running'):
                    raise RuntimeError('Headset hook stopped unexpectedly')
                pc_state = pc.exports_sync.status()
                if not pc_state['running']:
                    raise RuntimeError(pc_state.get('reason') or 'PC routing stopped unexpectedly')
                if not priority.exports_sync.status()['running']:
                    raise RuntimeError('Controller selection stopped unexpectedly')
                priority.exports_sync.renew(30)
                quest.exports_sync.renew(30)
                pc.exports_sync.renew(30)
                next_renew = time.monotonic() + 5
            time.sleep(0.25)
    except KeyboardInterrupt:
        print('Stopping...', flush=True)
    except Exception:
        failed = True
        raise
    finally:
        cleanup_errors = stop_scripts(scripts, detached, beat)
        for session in reversed(sessions):
            beat()
            try:
                session.detach()
            except Exception:
                pass
        progress[0] = None
        if owned_server:
            try:
                pids = root('pidof quest-hybrid-frida || true').split()
                for value in pids:
                    if value.isdecimal():
                        root('kill ' + value)
            except Exception as exc:
                cleanup_errors.append('Server cleanup: ' + str(exc))
        if forwarded:
            try:
                adb('forward', '--remove', 'tcp:27043')
            except Exception:
                pass
        if starter is not None:
            if starter.poll() is None:
                starter.terminate()
            starter.wait(timeout=5)
        STOP.unlink(missing_ok=True)
        kernel.CloseHandle(mutex)
        if cleanup_errors:
            print('Cleanup needs attention: ' + '; '.join(cleanup_errors), flush=True)
            if not failed:
                raise RuntimeError('Runtime restoration was not verified')
        else:
            print('Stopped; cleanup completed.', flush=True)


if __name__ == '__main__':
    main()
