"""Compile the wearer's current face/pupil calibration for the native headset worker."""
import json
from pathlib import Path
import struct

import numpy as np

from pupil_dilation import read_calibration
from universal_face import BROWS, CHEEKS, UniversalFace

CHANNELS = CHEEKS + BROWS + ['TongueOut']


def recent_benchmarks(root, count=4):
    found = []
    for session in sorted((Path(root) / 'captures').glob('benchmark*.qpsession.json'), key=lambda p: p.stat().st_mtime, reverse=True):
        try:
            if json.loads(session.read_text(encoding='utf-8')).get('completed'):
                found.append(session.with_name(session.name.removesuffix('.qpsession.json')))
        except (OSError, ValueError):
            continue
        if len(found) == count:
            break
    return found


def compile_profile(config, root):
    """One-time PC preparation; no image/model inference is needed on the PC during tracking."""
    root = Path(root)
    model = Path(config.get('universalModelPath') or root / 'models/universal-face-v2.npz')
    with np.load(model, allow_pickle=False) as archive:
        if not json.loads(str(archive['meta']))['approvedForOutput']:
            raise ValueError('This model is not approved for tracking output.')
    face = UniversalFace(model, config.get('faceEnrollment'), False,
        history=config.get('faceEnrollmentHistory') or (), benchmarks=recent_benchmarks(root))
    try:
        if config.get('tongueOutput', True) and face.tongue_map is None:
            raise ValueError('Complete the five tongue holds in face calibration before enabling the headset experiment.')
        arrays = []
        def add(value, shape):
            value = np.asarray(value, dtype='<f4')
            if value.shape != shape or not np.isfinite(value).all():
                raise ValueError('Unsupported or invalid headset calibration weights')
            arrays.append(value.tobytes())

        h = face.head
        a = np.where(face.present[:, None] > 0, face.anchors, h['missing'])
        add(h['w1'][:, :512] + h['w1'][:, 3584:4096], (512, 512))
        add(h['b1'] + h['w1'][:, 512:3584] @ a.ravel() - h['w1'][:, 3584:4096] @ a[0]
            + h['w1'][:, 4096:] @ face.present, (512,))
        add(h['w2'], (256, 512)); add(h['b2'], (256,))
        cheeks = [face.names.index(name) for name in CHEEKS]
        add(h['w3'][cheeks], (4, 256)); add(h['b3'][cheeks], (4,))
        b = face.brow_head
        neutral, present = face.brow_neutral
        neutral = neutral if present else b['missing']
        add(b['w1'][:, :480] + b['w1'][:, 480:960], (128, 480))
        add(b['b1'] - b['w1'][:, 480:960] @ neutral + b['w1'][:, 960] * present, (128,))
        add(b['w2'], (8, 128)); add(b['b2'], (8,))
        capabilities = 0
        if face.tongue_map is not None:
            mean, scale, weights, gains = face.tongue_map
            mapping = weights.T / scale
            add(mapping, (2, 512)); add(-(mapping @ mean), (2,)); add(gains, (4,))
            capabilities |= 1
        else:
            add(np.zeros((2, 512)), (2, 512)); add(np.zeros(2), (2,)); add(np.ones(4), (4,))
        if face.puff_camera is not None:
            rest, direction, camera, level = face.puff_camera
            amount = face.puff_amount
            capabilities |= 2
        else:
            rest, direction, camera, level, amount = np.zeros((2, 256)), np.zeros((2, 256)), np.zeros(2), np.full(2, .5), (0., 1.)
        add(rest, (2, 256)); add(direction, (2, 256)); add(camera, (2,)); add(level, (2,)); add(amount, (2,))
        add([face.events.anchor_neutral.get(n, 0.) for n in CHANNELS], (13,))
        add([face.events.reach.get(n, 1.) for n in CHANNELS], (13,))
        add([np.clip(float(config.get('faceEventBias') or 0), -.1, .1)], (1,))
        path = root / 'calibration/qpro-pupil-dilation.json'
        profile = read_calibration(path) if path.exists() else None
        if profile is not None:
            capabilities |= 8
        add(profile['small'] if profile else [10., 10.], (2,))
        add(profile['large'] if profile else [60., 60.], (2,))
        data = b''.join(arrays)
        return struct.pack('<8sII', b'QFTHP002', capabilities, len(data)) + data
    finally:
        face.close()
