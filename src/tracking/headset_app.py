"""One-time install/pairing and owned cleanup for the standalone headset app."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import secrets
import shlex
import socket
import tempfile

from headset import RootShell, adb

PACKAGE = 'com.qftplus.headset'
DATA = '/data/user/0/' + PACKAGE


def installed():
    return 'package:' in adb('shell', f'pm path {PACKAGE} || true')


def stop(root):
    adb('shell', 'am', 'force-stop', PACKAGE)
    root.run('for p in $(pidof libqft_worker.so); do '
             'exe=$(readlink /proc/$p/exe); case "$exe" in '
             '/data/app/*/com.qftplus.headset-*/lib/arm64/libqft_worker.so) kill "$p";; esac; done')
    for _ in range(20):
        if not root.run('pidof libqft_worker.so || true').strip():
            return
        root.run('sleep 0.25')
    raise RuntimeError('The headset worker did not stop; cleanup will be retried.')


def remove(root):
    if installed():
        stop(root)
        if 'Success' not in adb('uninstall', PACKAGE):
            raise RuntimeError('Android did not confirm removal of QFT+ Headset.')
    if installed():
        raise RuntimeError('Headset app removal is incomplete.')
    print('HEADSET_APP_REMOVED', flush=True)


def install(root, runtime, config):
    apk = runtime / 'headset/QFTPlus-Headset.apk'
    expected = (apk.with_suffix('.sha256')).read_text().strip()
    if hashlib.sha256(apk.read_bytes()).hexdigest() != expected:
        raise ValueError('Headset app failed verification. Reinstall QFT+.')
    key = config.get('headsetPairKey', '')
    if not re.fullmatch('[0-9a-f]{64}', key):
        raise ValueError('Missing headset pairing key. Enable the experiment from Settings.')
    addresses = adb('shell', 'ip', '-4', '-o', 'addr', 'show', 'wlan0')
    match = re.search(r'\binet (\d+\.\d+\.\d+\.\d+)/', addresses)
    if not match:
        raise ValueError('Connect the headset and PC to the same Wi-Fi network for automatic pairing.')
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as route:
        route.connect((match[1], 27276))
        host = route.getsockname()[0]
    fresh = not installed()
    if not fresh:
        stop(root)
    print('HEADSET_APP_INSTALLING', flush=True)
    if 'Success' not in adb('install', '-r', str(apk), timeout=180):
        raise RuntimeError('Android did not install the headset app.')
    tongue = bool(config.get('tongueOutput', False))
    if not root.run(f'test -f {DATA}/files/model/profile.bin && echo PRESENT', check=False):
        from headset_profile import compile_profile
        options = dict(config)
        try:
            profile = compile_profile(options, runtime)
        except ValueError:
            options['tongueOutput'] = tongue = False
            profile = compile_profile(options, runtime)
        scratch = '/data/local/tmp/qft-app-profile-' + secrets.token_hex(12)
        try:
            with tempfile.TemporaryDirectory(prefix='qft-app-profile-') as temporary:
                path = Path(temporary) / 'profile.bin'
                path.write_bytes(profile)
                adb('push', str(path), scratch)
            if root.run('sha256sum ' + scratch).split()[0] != hashlib.sha256(profile).hexdigest():
                raise RuntimeError('Calibration transfer failed verification.')
            root.run(f'mkdir -p {DATA}/files/model && mv {scratch} {DATA}/files/model/profile.bin && '
                     f'chown -R $(stat -c %u {DATA}):$(stat -c %g {DATA}) {DATA}/files && chmod 600 {DATA}/files/model/profile.bin')
        finally:
            root.run('rm -f ' + scratch, check=False)
    broadcast = ('am broadcast --include-stopped-packages -n ' + PACKAGE + '/.SetupReceiver '
                 '--es host ' + shlex.quote(host) + ' --ei port 27276 --es key ' + shlex.quote(key) + ' --ez enabled true')
    if fresh:
        broadcast += (' --ez pupils ' + str(bool(config.get('pupilDilation', False))).lower() +
                      ' --ez tongue ' + str(tongue).lower())
    if 'QFT_PAIRED' not in root.run(broadcast, display='pairing broadcast'):
        raise RuntimeError('Headset app did not confirm pairing.')
    if fresh:
        adb('shell', 'am', 'start', '-n', PACKAGE + '/.MainActivity')
    print('HEADSET_APP_READY', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('setup', 'cleanup'))
    parser.add_argument('--config', type=Path, required=True)
    args = parser.parse_args()
    root = RootShell()
    try:
        if args.action == 'cleanup':
            remove(root)
        else:
            install(root, Path(__file__).resolve().parent, json.loads(args.config.read_text(encoding='utf-8-sig')))
    finally:
        root.close()


if __name__ == '__main__':
    main()
