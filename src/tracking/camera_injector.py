"""Load the resident camera worker into the headset's sensor service using the bundled Frida injector."""
from contextlib import ExitStack
from pathlib import Path
import threading

import frida
from headset import RootShell, adb, start_frida_server, stop_frida_server

SERVER = 'quest-camera-frida'
LIBRARY = '/data/local/tmp/libquestpro-camera-streamer-v9.so'


def inject() -> None:
    with ExitStack() as cleanup:
        root = RootShell()
        cleanup.callback(root.close)
        stop_frida_server(root, SERVER)
        pid = int(root.run('pidof vendor.oculus.hardware.sensors@1.0-service', timeout=15).strip())
        print(f'PROVIDER_PID={pid}', flush=True)
        if Path(LIBRARY).name in root.run(f'cat /proc/{pid}/maps', timeout=15):
            print('INJECTION_ALREADY_ACTIVE', flush=True)
            return
        device, port = start_frida_server(root, SERVER, 27044)
        cleanup.callback(stop_frida_server, root, SERVER)
        cleanup.callback(adb, 'forward', '--remove', f'tcp:{port}')
        cancellable = frida.Cancellable()
        timer = threading.Timer(25, cancellable.cancel)
        timer.start()
        try:
            with cancellable:
                device.inject_library_file(pid, LIBRARY, 'qft_streamer_main', '')
        finally:
            timer.cancel()
        print('INJECTION_OK', flush=True)


if __name__ == '__main__':
    inject()
