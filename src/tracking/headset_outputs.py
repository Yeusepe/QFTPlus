"""Transport for the headset's completed tracking results; no PC inference or pupil detection."""
import json
import math
from pathlib import Path
import secrets
import socket
import struct

from headset_model import upload_profile
from headset_profile import CHANNELS
from label_capture import fresh_values
from pupil_dilation import PACKET as PUPIL_PACKET
from pupil_preview import Pupil
from tongue_output import TongueBroadcaster
from universal_face import BROWS, CHEEKS, FAMILIES, RAISES

NATIVE_NAMES = (
    'BrowLowererL BrowLowererR CheekPuffL CheekPuffR CheekRaiserL CheekRaiserR CheekSuckL CheekSuckR '
    'ChinRaiserB ChinRaiserT DimplerL DimplerR EyesClosedL EyesClosedR EyesLookDownL EyesLookDownR '
    'EyesLookLeftL EyesLookLeftR EyesLookRightL EyesLookRightR EyesLookUpL EyesLookUpR InnerBrowRaiserL InnerBrowRaiserR '
    'JawDrop JawSidewaysLeft JawSidewaysRight JawThrust LidTightenerL LidTightenerR LipCornerDepressorL LipCornerDepressorR '
    'LipCornerPullerL LipCornerPullerR LipFunnelerLb LipFunnelerLt LipFunnelerRb LipFunnelerRt LipPressorL LipPressorR '
    'LipPuckerL LipPuckerR LipStretcherL LipStretcherR LipSuckLb LipSuckLt LipSuckRb LipSuckRt LipTightenerL LipTightenerR '
    'LipsToward LowerLipDepressorL LowerLipDepressorR MouthLeft MouthRight NoseWrinklerL NoseWrinklerR '
    'OuterBrowRaiserL OuterBrowRaiserR UpperLidRaiserL UpperLidRaiserR UpperLipRaiserL UpperLipRaiserR '
    'TongueTipInterdental TongueTipAlveolar TongueFrontDorsalPalate TongueMidDorsalPalate TongueBackDorsalVelar TongueOut TongueRetreat'
).split()
CONTROL = struct.Struct('<4IQf70fI')
RESULT = struct.Struct('<5I61f')


def control_packet(face, raw_fps, sample, names, now_ns, *, preview=False, calibrating=False):
    native = fresh_values(sample, names, now_ns)
    age = max(0., (now_ns - sample['arrivalMonotonicNs']) / 1e9) if native is not None else 1000.
    sequence = int(sample['arrivalMonotonicNs']) if native is not None else 0
    flags = (face.flags if face else 0) | (32 if preview else 0) | (64 if calibrating else 0)
    return CONTROL.pack(0x43544651, 2, raw_fps, face.revision if face else 0, sequence, age,
        *((native or {}).get(name, math.nan) for name in NATIVE_NAMES), flags)


class PupilOutput:
    def __init__(self, enabled):
        self.enabled = enabled
        self.detected_pupils = [None, None]
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.closed = False

    def send(self, valid=False, values=(.5, .5)):
        if self.enabled and not self.closed:
            self.socket.sendto(PUPIL_PACKET.pack(b'QPPD', 1, int(valid), 0, *values), ('127.0.0.1', 27275))

    def close(self):
        if not self.closed:
            self.send()
            self.closed = True
            self.socket.close()


class HeadsetOutputs:
    def __init__(self, config, root, *, log=None):
        upload_profile(config, root)
        self.revision = secrets.randbits(32) or 1
        self.enabled = config.get('extraFaceOutput', True) is not False
        self.families = [f for f in FAMILIES if config.get(f'extraFaceOutput-{f}', True) is not False]
        self.sent = [n for f in self.families for n in FAMILIES[f] if n not in RAISES]
        self.pupils = PupilOutput(config.get('pupilDilation', False))
        self.tongue = TongueBroadcaster(enabled=config.get('tongueOutput', True))
        self.flags = int(self.enabled) | (2 if self.tongue.enabled else 0) | (4 if self.pupils.enabled else 0)
        self.flags |= (8 if 'puff' in self.families else 0) | (16 if 'brows' in self.families else 0)
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.closed = False
        self.log_path, self.log_file, self.log_part = Path(log) if log else None, None, 0
        print('INFERENCE_BACKEND HeadsetDSP personalized=headset pupils=headset', flush=True)

    def update(self, payload, sample, now_ns):
        version, flags, revision, status, on, *v = RESULT.unpack(payload)
        if version != 2 or flags > 127 or status > 31 or on >= (1 << 13) or not all(map(math.isfinite, v)):
            raise ValueError('Invalid headset tracking result')
        if revision != self.revision:
            return None
        expressions, shares, tongue, pupils = v[:12], v[12:16], v[16:19], v[19:21]
        if any(x < -.00001 or x > 1.00001 for x in expressions + shares + pupils) or any(abs(x)>1.00001 for x in tongue):
            raise ValueError('Headset tracking result outside its valid range')
        detected = []
        for i in range(2):
            valid, x, y, a, b, angle, diameter = v[21+7*i:28+7*i]
            if valid not in (0., 1.) or valid and not (8 <= x <= 392 and 8 <= y <= 392 and 10 <= diameter <= 60 and a > 0 and b > 0):
                raise ValueError('Invalid headset pupil geometry')
            detected.append(Pupil(((x, y), (a, b), angle), diameter) if valid else None)
        if self.enabled:
            packet = {'version': 1, 'enabled': True, 'values': {n: expressions[(CHEEKS+BROWS).index(n)] for n in self.sent}}
            if 'brows' in self.families:
                packet['shares'] = dict(zip(BROWS[:4], [round(x, 4) for x in shares]))
            self.socket.sendto(json.dumps(packet).encode(), ('127.0.0.1', 27275))
        self.tongue.send(*tongue, bool(status & 4))
        self.pupils.send(bool(status & 8), pupils)
        if status & 16:
            self.pupils.detected_pupils = detected
        if self.log_path is not None:
            if self.log_file is None or self.log_file.tell() > (1 << 30):
                if self.log_file is not None:
                    self.log_file.close(); self.log_part += 1
                path = self.log_path if not self.log_part else self.log_path.with_name(f'{self.log_path.stem}.{self.log_part}{self.log_path.suffix}')
                path.parent.mkdir(parents=True, exist_ok=True)
                self.log_file = path.open('a', encoding='utf-8', buffering=1 << 16)
            self.log_file.write(json.dumps(dict(t=now_ns, raw=dict(zip(CHANNELS, v[35:48])), out=dict(zip(CHANNELS, expressions+[float(bool(status & 4))])),
                on=[n for i, n in enumerate(CHANNELS) if on & (1 << i)], speaking=bool(status & 2), native=bool(status & 1),
                seq=sample.get('sourceSequence') if sample else None, neutral=dict(zip(CHANNELS, v[48:61])))) + '\n')
        return detected if status & 16 else None

    def close(self):
        if self.closed:
            return
        self.closed = True
        self.tongue.close()
        if self.enabled:
            self.socket.sendto(b'{"version":1,"enabled":false,"values":{}}', ('127.0.0.1', 27275))
        self.socket.close()
        if self.log_file is not None:
            self.log_file.close()
