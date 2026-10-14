"""One-time install/pairing and owned cleanup for the standalone headset app."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shlex
import socket
import tempfile
import xml.etree.ElementTree as ET
import zipfile

from headset import RootShell, adb

PACKAGE = 'com.qftplus.headset'
DATA = '/data/user/0/' + PACKAGE
BACKUP_FILES = {'shared_prefs/settings.xml': 256 * 1024, 'files/output-settings.json': 1024 * 1024,
                'files/model/profile.bin': 8 * 1024 * 1024}


def backup_location(root, config):
    serial = root.run('getprop ro.serialno').strip()
    if not re.fullmatch(r'[A-Za-z0-9_-]{1,128}', serial) or serial != config.get('headsetModelDevice'):
        raise ValueError('HeadsetBackupWrongDevice')
    return Path(os.environ['LOCALAPPDATA']) / 'QFTPlus/headset-backups' / ('quest-' + serial + '.zip'), serial


def validate_files(files):
    for name, data in files.items():
        if name not in BACKUP_FILES or not 0 < len(data) <= BACKUP_FILES[name]:
            raise ValueError('Invalid backup file')
        if name.endswith('.xml'):
            if b'<!' in data or ET.fromstring(data).tag != 'map':
                raise ValueError('Invalid settings')
        elif name.endswith('.json'):
            if not isinstance(json.loads(data), dict):
                raise ValueError('Invalid adjustments')
        elif not data.startswith(b'QFTHP') or len(data) < 32:
            raise ValueError('Invalid calibration')


def read_backup(path, serial):
    with zipfile.ZipFile(path) as archive:
        entries = archive.infolist()
        names = [entry.filename for entry in entries]
        if len(names) != len(set(names)) or 'manifest.json' not in names:
            raise ValueError('Invalid backup entries')
        for entry in entries:
            if entry.filename not in {'manifest.json', *BACKUP_FILES} or entry.file_size > BACKUP_FILES.get(entry.filename, 4096):
                raise ValueError('Invalid backup entry')
        manifest = json.loads(archive.read('manifest.json'))
        if manifest.get('version') != 1 or manifest.get('serial') != serial or set(manifest['files']) != set(names) - {'manifest.json'}:
            raise ValueError('Incompatible backup')
        files = {name: archive.read(name) for name in manifest['files']}
        if not files or any(hashlib.sha256(data).hexdigest() != manifest['files'][name] for name, data in files.items()):
            raise ValueError('Backup verification failed')
        validate_files(files)
        return files


def backup(root, config):
    path, serial = backup_location(root, config)
    files = {}
    for name, limit in BACKUP_FILES.items():
        source = DATA + '/' + name
        size = root.run(f'if test -f {source}; then stat -c %s {source}; fi').strip()
        if not size:
            continue
        if not 0 < int(size) <= limit:
            raise ValueError('Invalid backup size')
        data = base64.b64decode(''.join(root.run('base64 ' + source, timeout=30).split()), validate=True)
        if len(data) != int(size) or hashlib.sha256(data).hexdigest() != root.run('sha256sum ' + source).split()[0]:
            raise ValueError('Backup transfer failed verification')
        if name.endswith('.xml'):
            if b'<!' in data:
                raise ValueError('Invalid settings')
            settings = ET.fromstring(data)
            for entry in list(settings):
                if entry.get('name') in {'host', 'port', 'key', 'enabled'}:
                    settings.remove(entry)
            data = ET.tostring(settings, encoding='utf-8', xml_declaration=True)
        files[name] = data
    if not files:
        return
    validate_files(files)
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.pending-', dir=path.parent) as folder:
        pending = Path(folder) / path.name
        manifest = {'version': 1, 'serial': serial, 'files': {name: hashlib.sha256(data).hexdigest() for name, data in files.items()}}
        with zipfile.ZipFile(pending, 'w', zipfile.ZIP_DEFLATED) as archive:
            archive.writestr('manifest.json', json.dumps(manifest))
            for name, data in files.items():
                archive.writestr(name, data)
        read_backup(pending, serial)
        pending.replace(path)
    print('HEADSET_BACKUP_SAVED', flush=True)


def restore(root, files):
    validate_files(files)
    scratch = '/data/local/tmp/qft-app-restore-' + secrets.token_hex(12)
    root.run(f'mkdir -m 700 {scratch} && chown 2000:2000 {scratch}')
    try:
        with tempfile.TemporaryDirectory(prefix='qft-app-restore-') as folder:
            for index, (name, data) in enumerate(files.items()):
                local = Path(folder) / str(index)
                local.write_bytes(data)
                remote = scratch + '/' + str(index)
                adb('push', str(local), remote)
                if root.run('sha256sum ' + remote).split()[0] != hashlib.sha256(data).hexdigest():
                    raise ValueError('Restore transfer failed verification')
            root.run(f'mkdir -p {DATA}/files/model {DATA}/shared_prefs')
            for index, name in enumerate(files):
                root.run(f'mv {scratch}/{index} {DATA}/{name} && chmod 600 {DATA}/{name}')
            paths = ' '.join([DATA + '/files', DATA + '/files/model', DATA + '/shared_prefs', *[DATA + '/' + name for name in files]])
            root.run(f'chown $(stat -c %u {DATA}):$(stat -c %g {DATA}) {paths} && restorecon -RF {DATA}/files {DATA}/shared_prefs')
    finally:
        root.run('rm -rf ' + scratch, check=False)
    print('HEADSET_BACKUP_RESTORED', flush=True)


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


def remove(root, config, save=True):
    if installed():
        stop(root)
        if save:
            try:
                backup(root, config)
            except Exception as error:
                raise RuntimeError('HeadsetBackupFailed') from error
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
    saved = None
    if fresh:
        try:
            path, serial = backup_location(root, config)
        except Exception as error:
            if str(error) == 'HeadsetBackupWrongDevice':
                raise
            raise RuntimeError('HeadsetRestoreFailed') from error
        try:
            if path.exists():
                saved = read_backup(path, serial)
        except Exception as error:
            raise RuntimeError('HeadsetBackupUnreadable') from error
    if not fresh:
        stop(root)
        try:
            backup(root, config)
        except Exception as error:
            raise RuntimeError('HeadsetBackupFailed') from error
    print('HEADSET_APP_INSTALLING', flush=True)
    if 'Success' not in adb('install', '-r', str(apk), timeout=180):
        raise RuntimeError('Android did not install the headset app.')
    if saved:
        try:
            restore(root, saved)
        except Exception as error:
            raise RuntimeError('HeadsetRestoreFailed') from error
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
                 '--es host ' + shlex.quote(host) + ' --ei port 27276 --es key ' + shlex.quote(key))
    if fresh and not saved:
        broadcast += (' --ez pupils ' + str(bool(config.get('pupilDilation', False))).lower() +
                      ' --ez tongue ' + str(tongue).lower())
    if 'QFT_PAIRED' not in root.run(broadcast, display='pairing broadcast'):
        raise RuntimeError('Headset app did not confirm pairing.')
    if fresh:
        adb('shell', 'am', 'start', '-n', PACKAGE + '/.MainActivity')
    print('HEADSET_APP_READY', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('setup', 'cleanup', 'rollback'))
    parser.add_argument('--config', type=Path, required=True)
    args = parser.parse_args()
    root = RootShell()
    try:
        config = json.loads(args.config.read_text(encoding='utf-8-sig'))
        if args.action != 'setup':
            remove(root, config, save=args.action == 'cleanup')
        else:
            install(root, Path(__file__).resolve().parent, config)
    except Exception as error:
        if str(error) not in {'HeadsetBackupFailed', 'HeadsetRestoreFailed', 'HeadsetBackupWrongDevice', 'HeadsetBackupUnreadable'}:
            raise
        print(str(error), flush=True)
        raise SystemExit(1) from None
    finally:
        root.close()


if __name__ == '__main__':
    main()
