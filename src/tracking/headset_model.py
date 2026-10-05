"""Install, verify and remove the opt-in headset face-model experiment."""
import argparse
import hashlib
import json
from pathlib import Path
import shlex
import tempfile
import secrets

import numpy as np
from headset import RootShell, adb

DIRECTORY = '/data/local/tmp/qft-headset-model-v1'
STAGING = DIRECTORY + '.staging'
MARKER = 'QFT_HEADSET_MODEL_V1'
FILES = ('qft-headset-model', 'model.bin', 'tail.onnx', 'libonnxruntime.so', 'libopencv_java5.so', 'libc++_shared.so', 'manifest.json')
PERSONAL = ('profile.bin', 'profile.bin.tmp')


def calibration(config, manifest):
    model = Path(config.get('universalModelPath') or Path(__file__).resolve().parent / 'models/universal-face-v2.npz')
    if hashlib.sha256(model.with_suffix('.area.onnx').read_bytes()).hexdigest() != manifest['model_sha256']:
        raise ValueError('This face model is not supported by the headset experiment. Select the bundled model first.')
    enrollment = config.get('faceEnrollment')
    if not enrollment:
        raise ValueError('Complete face and tongue calibration before enabling Experimental on Headset Model.')
    with np.load(enrollment, allow_pickle=False) as archive:
        if json.loads(str(archive['meta']))['schema'] != 'face-enrollment-v1':
            raise ValueError('Face calibration is not compatible with the headset experiment.')
        if 'slot_neutral' not in archive or archive['slot_neutral'].ndim != 3 or archive['slot_neutral'].shape[1:] != (400, 2000) or not len(archive['slot_neutral']):
            raise ValueError('Complete face calibration before enabling the headset experiment.')
        if not config.get('tongueOutput', True):
            return
        for pose in ('tongue_out', 'tongue_up', 'tongue_down', 'tongue_left', 'tongue_right'):
            key = 'slot_' + pose
            if key not in archive or archive[key].ndim != 3 or archive[key].shape[1:] != (400, 2000) or not len(archive[key]):
                raise ValueError('Complete all tongue directions in face calibration before enabling the headset experiment.')


def remove(root, directory):
    if directory not in (DIRECTORY, STAGING):
        raise ValueError('Not an experiment directory')
    d = shlex.quote(directory)
    exists = root.run(f'if [ -e {d} ] || [ -L {d} ]; then echo PRESENT; fi').strip()
    if not exists:
        return
    owner = root.run(f'test ! -L {d} && test -d {d} && cat {d}/.qft-owner && echo', check=False)
    if owner is None or owner.strip() != MARKER:
        raise RuntimeError('Refusing to remove an unrecognized headset directory: ' + directory)
    if directory == DIRECTORY:
        root.run(f'if [ -x {d}/qft-headset-model ]; then LD_LIBRARY_PATH={d}:/vendor/lib64 {d}/qft-headset-model --stop || true; fi; '
                 f'for p in $(pidof qft-headset-model); do '
                 f'if [ "$(readlink /proc/$p/exe)" = {d}/qft-headset-model ]; then kill "$p"; fi; done')
    names = ' '.join(shlex.quote(directory + '/' + name) for name in FILES + PERSONAL)
    root.run(f'rm -f {names} && rm -f {d}/.qft-owner && '
             f'(rmdir {d} || {{ printf %s {MARKER} > {d}/.qft-owner; exit 1; }})')


def setup(root, payload, config):
    manifest = json.loads((payload / 'manifest.json').read_text(encoding='utf-8'))
    if manifest.get('version') != 1 or set(manifest.get('files', {})) != set(FILES) - {'manifest.json'}:
        raise ValueError('The packaged headset experiment is incomplete. Reinstall QFT+.')
    calibration(config, manifest)
    for name, expected in manifest['files'].items():
        if hashlib.sha256((payload / name).read_bytes()).hexdigest() != expected:
            raise ValueError('Headset experiment component failed verification: ' + name)
    remove(root, STAGING)
    names = ' '.join(STAGING + '/' + name for name in FILES + PERSONAL)
    root.undo_on_exit(f'if [ ! -L {STAGING} ] && [ "$(cat {STAGING}/.qft-owner 2>/dev/null)" = {MARKER} ]; then '
                      f'rm -f {names}; rm -f {STAGING}/.qft-owner; '
                      f'rmdir {STAGING} || printf %s {MARKER} > {STAGING}/.qft-owner; fi')
    try:
        adb('shell', f'mkdir {STAGING} && chmod 700 {STAGING}')
        root.run(f"printf '%s\\n' {MARKER} > {STAGING}/.qft-owner")
        for name in FILES:
            print('HEADSET_MODEL_COPY ' + name, flush=True)
            adb('push', str(payload / name), STAGING + '/' + name, timeout=90)
        for name, expected in manifest['files'].items():
            actual = root.run('sha256sum ' + shlex.quote(STAGING + '/' + name)).split()[0]
            if actual != expected:
                raise RuntimeError('Headset copy failed verification: ' + name)
        upload_profile(config, payload.parent, root=root, directory=STAGING)
        root.run(f'chown -R root:root {STAGING}; chmod 700 {STAGING}; chmod 755 {STAGING}/qft-headset-model')
        check = root.run(f'LD_LIBRARY_PATH={STAGING}:/vendor/lib64 {STAGING}/qft-headset-model --check-model {STAGING}', timeout=60)
        print(check, flush=True)
        if 'MODEL_READY ' not in check:
            raise RuntimeError('The headset did not confirm model execution. The experiment was not enabled.')
        remove(root, DIRECTORY)
        root.run(f'mv {STAGING} {DIRECTORY}')
    except BaseException:
        remove(root, STAGING)
        raise
    print('HEADSET_MODEL_READY', flush=True)


def upload_profile(config, runtime, *, root=None, directory=DIRECTORY):
    from headset_profile import compile_profile
    if directory not in (DIRECTORY, STAGING):
        raise ValueError('Not an experiment directory')
    calibration(config, json.loads((Path(runtime) / 'headset-model/manifest.json').read_text(encoding='utf-8-sig')))
    data = compile_profile(config, runtime)
    owned_root = root is None
    root = RootShell() if owned_root else root
    scratch = '/data/local/tmp/qft-headset-profile-' + secrets.token_hex(12)
    created = False
    try:
        d = shlex.quote(directory)
        owner = root.run(f'test ! -L {d} && cat {d}/.qft-owner && echo', check=False)
        if owner is None or owner.strip() != MARKER:
            raise RuntimeError('Headset experiment ownership could not be verified.')
        adb('shell', f'mkdir {scratch} && chmod 700 {scratch}')
        created = True
        root.undo_on_exit(f'rm -f {scratch}/profile.bin; rmdir {scratch} 2>/dev/null || true')
        with tempfile.TemporaryDirectory(prefix='qft-headset-profile-') as temporary:
            path = Path(temporary) / 'profile.bin'
            path.write_bytes(data)
            adb('push', str(path), scratch + '/profile.bin')
        digest = root.run(f'sha256sum {scratch}/profile.bin').split()[0]
        if digest != hashlib.sha256(data).hexdigest():
            raise RuntimeError('Headset calibration copy failed verification.')
        root.run(f'chmod 600 {scratch}/profile.bin && chown root:root {scratch}/profile.bin && mv {scratch}/profile.bin {d}/profile.bin')
    finally:
        try:
            if created:
                root.run(f'rm -f {scratch}/profile.bin; rmdir {scratch}', check=False)
        finally:
            if owned_root:
                root.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=('setup', 'cleanup', 'validate', 'ensure'))
    parser.add_argument('--config', type=Path)
    args = parser.parse_args()
    payload = Path(__file__).resolve().parent / 'headset-model'
    if args.action == 'validate':
        calibration(json.loads(args.config.read_text(encoding='utf-8-sig')),
                    json.loads((payload / 'manifest.json').read_text()))
        return
    root = RootShell()
    try:
        if args.action == 'ensure':
            manifest = json.loads((payload / 'manifest.json').read_text())
            calibration(json.loads(args.config.read_text(encoding='utf-8-sig')), manifest)
            owner = root.run(f'test ! -L {DIRECTORY} && cat {DIRECTORY}/.qft-owner && echo', check=False)
            verified = owner is not None and owner.strip() == MARKER
            for name, digest in manifest['files'].items():
                text = root.run('sha256sum ' + shlex.quote(DIRECTORY + '/' + name), check=False)
                if not text or text.split()[0] != digest:
                    verified = False
                    break
            if verified:
                upload_profile(json.loads(args.config.read_text(encoding='utf-8-sig')), payload.parent, root=root)
                print('HEADSET_MODEL_READY', flush=True)
                return
        if args.action in ('setup', 'ensure'):
            setup(root, payload, json.loads(args.config.read_text(encoding='utf-8-sig')))
        else:
            remove(root, STAGING)
            remove(root, DIRECTORY)
            print('HEADSET_MODEL_REMOVED', flush=True)
    finally:
        root.close()


if __name__ == '__main__':
    main()
