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
import sys
import time

HERE = Path(__file__).resolve().parent
CONTEXT = json.loads(Path(os.environ['QROOT_CONTEXT']).read_text(encoding='utf-8-sig')) if os.environ.get('QROOT_CONTEXT') else {}
ADB = Path(CONTEXT.get('adb', os.environ.get('QPRO_ADB', str(HERE.parent / 'platform-tools/adb.exe'))))
STOP = Path(CONTEXT.get('stopFile', str(HERE / 'hybrid.stop')))
DRIVER = Path(CONTEXT.get('settings', {}).get('driverPath') or str(Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / 'Virtual Desktop Streamer/OpenVRDriver/bin/win64/driver_VirtualDesktop.dll'))
DRIVER_HASH = 'ad3c99c7f7346613d4c7106fb94476be5859ca17a637a11cfc156184d466f36f'
SERVER = '/data/local/tmp/quest-hybrid-frida'
sys.path.insert(0, str(HERE / 'python-deps'))
import frida


def load_hand_hook(session, receive, wait_for_startup=None):
    """The experimental spawn starts before Mono and its settings singleton."""
    end = time.monotonic() + (60 if wait_for_startup else 0)
    while True:
        startup_errors = []
        script = session.create_script((HERE / 'vd_hand_hook.js').read_text())
        def startup_message(event, data):
            if event['type'] == 'error':
                startup_errors.append(event.get('description', str(event)))
        script.on('message', startup_message)
        try:
            script.load()
            script.exports_sync.status()
            if startup_errors:
                raise RuntimeError(startup_errors[-1])
        except Exception:
            script.unload()
            expected = startup_errors and any(text in startup_errors[-1] for text in
                ('unable to find module', 'is not loaded', 'Settings singleton is null'))
            if not wait_for_startup or not expected or time.monotonic() >= end:
                raise RuntimeError(startup_errors[-1] if startup_errors else 'Headset hook did not initialize')
            wait_for_startup()
            time.sleep(0.5)
            continue
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


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--target', required=True)
    ap.add_argument('--check', action='store_true')
    ap.add_argument('--seconds', type=int, help='Maximum session length; default 12 hours, or 5 minutes for the controller test')
    ap.add_argument('--restart-vd-for-controller-test', action='store_true',
        help='EXPERIMENTAL: restart headset Virtual Desktop once to test detached-controller fallback')
    ap.add_argument('--stop', action='store_true')
    args = ap.parse_args()
    if args.seconds is None:
        args.seconds = 300 if args.restart_vd_for_controller_test else 43200
    if args.check:
        if not DRIVER.is_file() or hashlib.sha256(DRIVER.read_bytes()).hexdigest() != DRIVER_HASH:
            raise RuntimeError('Unsupported Virtual Desktop PC driver. Select the verified driver path; no hook was applied.')
        print('Hybrid runtime and driver compatibility verified; live fallback remains experimental.')
        return
    if args.stop:
        STOP.touch()
        print('Stop requested. Original settings will be restored within a few seconds.')
        return
    if not 1 <= args.seconds <= 43200:
        ap.error('Use 1..43200 seconds')
    if not re.fullmatch(r'[A-Za-z0-9_.:-]+', args.target):
        ap.error('Invalid ADB target')
    if args.restart_vd_for_controller_test:
        ap.error('Controller restart experiment is disabled after a live startup freeze. Ordinary hybrid tracking is unchanged.')
    fallback_source = (HERE / 'vd_controller_fallback.js').read_text() if args.restart_vd_for_controller_test else None

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
    starter = None
    fallback = None
    priority = None
    quest = None
    suspended_pid = None
    device = None
    owned_server = False
    forwarded = False
    STOP.unlink(missing_ok=True)

    def adb(*commands):
        return subprocess.check_output([str(ADB), '-s', args.target, *commands],
            text=True, stderr=subprocess.STDOUT, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW).strip()

    def root(command):
        return adb('shell', 'su', '-c', command)

    def message(source):
        def receive(event, data):
            if event['type'] == 'error':
                errors.append(event.get('description', str(event)))
                print(source, 'ERROR:', errors[-1], flush=True)
                return
            payload = event.get('payload', {})
            if payload.get('event') == 'fallback-error':
                errors.append(payload['error'])
            if payload.get('event') in ('applied', 'stopped', 'route', 'restored', 'fallback-ready', 'fallback-error'):
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
        if not args.restart_vd_for_controller_test:
            int(adb('shell', 'pidof', 'VirtualDesktop.Android'))

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

        if args.restart_vd_for_controller_test:
            print('Controller fallback test: restarting headset Virtual Desktop once. Reconnect to this PC.', flush=True)
            suspended_pid = device.spawn(['VirtualDesktop.Android'])
            quest_session = device.attach(suspended_pid, persist_timeout=60)
        else:
            pid = int(adb('shell', 'pidof', 'VirtualDesktop.Android'))
            try:
                quest_session = device.attach(pid, persist_timeout=60)
            except (frida.ProcessNotFoundError, frida.ProcessNotRespondingError):
                current_pid = int(adb('shell', 'pidof', 'VirtualDesktop.Android'))
                if current_pid == pid:
                    raise
                quest_session = device.attach(current_pid, persist_timeout=60)
        sessions.append(quest_session)
        priority = quest_session.create_script((HERE / 'vd_controller_priority.js').read_text())
        priority.on('message', message('Controllers'))
        priority.load()
        priority.exports_sync.status()
        scripts.append(priority)
        if args.restart_vd_for_controller_test:
            fallback = quest_session.create_script(fallback_source)
            fallback.on('message', message('Controllers'))
            fallback.load()
            scripts.append(fallback)
            device.resume(suspended_pid)
            suspended_pid = None

        def renew_fallback():
            state = fallback.exports_sync.status()
            if errors or state['error'] or not state['running']:
                raise RuntimeError(state['error'] or (errors[-1] if errors else 'Controller fallback stopped'))
            if STOP.exists():
                raise KeyboardInterrupt
            fallback.exports_sync.renew(30)
            return state

        if fallback:
            startup_end = time.monotonic() + 60
            while True:
                state = renew_fallback()
                if state['ready'] and state['spaces'] >= 2 and state['bindings']:
                    break
                if time.monotonic() >= startup_end:
                    raise RuntimeError('Virtual Desktop did not initialize controller paths within 60 seconds')
                time.sleep(0.5)
        quest = load_hand_hook(quest_session, message('Quest'), renew_fallback if fallback else None)
        pc_session = frida.get_local_device().attach('vrserver.exe', persist_timeout=60)
        sessions.append(pc_session)
        pc = pc_session.create_script((HERE / 'vd_pc_skeleton.js').read_text())
        pc.on('message', message('PC'))
        pc.load()
        scripts.append(pc)
        pc.exports_sync.start(30)
        scripts.append(quest)
        priority.exports_sync.start(30)
        quest.exports_sync.apply(30)
        from qroot_owner import owner_alive
        def renew_startup():
            if STOP.exists() or not owner_alive(CONTEXT.get('ownerPid')):
                raise KeyboardInterrupt
            if errors:
                raise RuntimeError(errors[-1])
            if fallback:
                renew_fallback()
            if not pc.exports_sync.status()['running']:
                raise RuntimeError('PC routing stopped before tracking began')
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
            if errors:
                raise RuntimeError(errors[-1])
            if time.monotonic() >= next_renew:
                if fallback:
                    renew_fallback()
                    state = fallback.exports_sync.status()
                    if not state['ready'] or not state['spaces'] or not state['bindings']:
                        raise RuntimeError('Detached controller paths were not initialized; test stopped')
                    print('Controllers ' + json.dumps(state), flush=True)
                if quest.exports_sync.status()['state'] not in ('starting', 'running'):
                    raise RuntimeError('Headset hook stopped unexpectedly')
                if not pc.exports_sync.status()['running']:
                    raise RuntimeError('PC routing stopped unexpectedly')
                if not priority.exports_sync.status()['running']:
                    raise RuntimeError('Controller selection stopped unexpectedly')
                priority.exports_sync.renew(30)
                quest.exports_sync.renew(30)
                pc.exports_sync.renew(30)
                next_renew = time.monotonic() + 5
            time.sleep(0.25)
    except KeyboardInterrupt:
        print('Stopping...', flush=True)
    finally:
        for script in reversed(scripts):
            try:
                script.exports_sync.stop()
                if script is quest:
                    for _ in range(80):
                        if script.exports_sync.status()['state'] == 'stopped':
                            break
                        time.sleep(0.1)
                    else:
                        errors.append('Headset restoration was not acknowledged. Restart Virtual Desktop on the headset.')
            except Exception as exc:
                errors.append(str(exc))
        for session in reversed(sessions):
            try:
                session.detach()
            except Exception:
                pass
        if suspended_pid is not None and device is not None:
            try:
                device.resume(suspended_pid)
            except Exception as exc:
                errors.append('Resume Virtual Desktop after startup failure: ' + str(exc))
        if owned_server:
            try:
                pids = root('pidof quest-hybrid-frida || true').split()
                for value in pids:
                    if value.isdecimal():
                        root('kill ' + value)
            except Exception as exc:
                errors.append('Server cleanup: ' + str(exc))
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
        if errors:
            print('Cleanup needs attention: ' + '; '.join(errors), flush=True)
            raise RuntimeError('Runtime restoration was not verified')
        else:
            if fallback:
                print('Stopped; original tracking settings restored. Restart headset Virtual Desktop normally to remove the added OpenXR bindings.', flush=True)
            else:
                print('Stopped; original tracking settings restored.', flush=True)


if __name__ == '__main__':
    main()
