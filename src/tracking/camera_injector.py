"""Load the resident camera worker using the bundled Frida injector."""
import argparse
from contextlib import ExitStack
import hashlib
from pathlib import Path
import subprocess
import threading

import frida
from native_raw_eye_probe import PersistentAdbRootShell

SERVER = '/data/local/tmp/quest-camera-frida'
LIBRARY = '/data/local/tmp/libquestpro-camera-streamer-v8.so'


def inject(adb: str) -> None:
    manager = frida.get_device_manager()
    def shell(command):
        return root.run(command, timeout=15).stdout.strip()
    def command(*args):
        return subprocess.check_output([adb, *args], text=True, timeout=30,
            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0)).strip()
    def stop_server():
        pids = shell('pidof quest-camera-frida || true').split()
        if pids:
            shell('kill ' + ' '.join(str(int(pid)) for pid in pids))
    with ExitStack() as cleanup:
        root = PersistentAdbRootShell(adb)
        cleanup.callback(root.close)
        pid = int(shell('pidof vendor.oculus.hardware.sensors@1.0-service'))
        print(f'PROVIDER_PID={pid}', flush=True)
        if Path(LIBRARY).name in shell(f'cat /proc/{pid}/maps'):
            print('INJECTION_ALREADY_ACTIVE', flush=True)
            return
        if shell('pidof quest-camera-frida || true'):
            raise RuntimeError('Camera injection is already running. Retry after it finishes.')
        bundled = Path(__file__).resolve().parent / 'hybrid/frida-server-17.18.0-android-arm64'
        expected = hashlib.sha256(bundled.read_bytes()).hexdigest()
        existing = shell(f'sha256sum {SERVER} 2>/dev/null || true').split()
        if not existing or existing[0] != expected:
            command('push', str(bundled), SERVER)
        shell(f'chmod 755 {SERVER}')
        cleanup.callback(stop_server)
        shell(f'{SERVER} --listen 127.0.0.1:27044 --disable-preload --daemonize')
        port = int(command('forward', 'tcp:0', 'tcp:27044'))
        cleanup.callback(command, 'forward', '--remove', f'tcp:{port}')
        cancellable = frida.Cancellable()
        timer = threading.Timer(25, cancellable.cancel)
        timer.start()
        try:
            with cancellable:
                device = manager.add_remote_device(f'127.0.0.1:{port}')
                cleanup.callback(manager.remove_remote_device, f'127.0.0.1:{port}')
                device.inject_library_file(pid, LIBRARY, 'qft_streamer_main', '')
        finally:
            timer.cancel()
        print('INJECTION_OK', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--adb', required=True)
    inject(parser.parse_args().adb)
