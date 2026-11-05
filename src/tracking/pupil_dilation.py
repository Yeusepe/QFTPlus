"""Calibrated relative pupil size, not a physical millimetre measurement."""
import json
import socket
import struct
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np

from pupil_preview import detect_pupil

PACKET = struct.Struct("<4sBBHff")
ADAPT, RAMP, BRIGHT, DARK = 30., 5., 5., 10.
CYCLE = 2*RAMP+BRIGHT+DARK
DURATION = ADAPT+2*CYCLE
LAG = .8
HOLD = 1.
STAGES = ["GuideLetYourEyesAdjust", "GuideLookAtTheCircle", "GuideLookAtTheCircle"]


def stimulus(t):
    if t < ADAPT:
        return 0.
    t = (t-ADAPT) % CYCLE
    if t < RAMP:
        return .5-.5*np.cos(np.pi*t/RAMP)
    if t < RAMP+BRIGHT:
        return 1.
    if t < 2*RAMP+BRIGHT:
        return .5+.5*np.cos(np.pi*(t-RAMP-BRIGHT)/RAMP)
    return 0.


def source_eyes(profile):
    eyes = profile.get("sourceEyes", [0, 1])
    if not isinstance(eyes, list) or not eyes or any(type(i) is not int for i in eyes) or eyes not in ([0], [1], [0, 1]):
        raise ValueError("GuideInvalidRelativePupilCalibration")
    return eyes


def fit_calibration(samples, selected=(0, 1)):
    times = np.asarray([s[0] for s in samples], dtype=float)
    eyes = np.asarray([s[1] for s in samples], dtype=float)
    selected = source_eyes({"sourceEyes": list(selected)})
    cycles = [times < ADAPT+CYCLE, times >= ADAPT+CYCLE]
    if not len(times):
        raise ValueError("GuideYourPupilsWereHiddenTooOftenAdjustTheHeadsetSo")
    if eyes.ndim != 3 or eyes.shape[1:] != (2, 4) or not np.isfinite(times).all():
        raise ValueError("GuideInvalidPupilCalibrationSamples")
    small, large = np.full(2, 10.), np.full(2, 60.)
    centers, ratios = np.full((2, 2), 200.), np.ones(2)
    response, counts = np.zeros((2, 2)), np.zeros((2, 2), dtype=int)
    levels = np.asarray([stimulus(t-LAG) for t in times])
    for eye in selected:
        valid = np.isfinite(eyes[:, eye]).all(axis=1)
        if np.isinf(eyes[:, eye]).any() or np.any(~valid & ~np.isnan(eyes[:, eye]).all(axis=1)):
            raise ValueError("GuideInvalidPupilCalibrationSamples")
        masks = [c & valid for c in cycles]
        counts[:, eye] = [c.sum() for c in masks]
        if min(counts[:, eye]) < 80:
            raise ValueError("GuideYourPupilsWereHiddenTooOftenAdjustTheHeadsetSo")
        # Fit the first round; the second round checks parameters it did not help fit.
        small[eye], large[eye] = np.percentile(eyes[masks[0], eye, 0], (5, 95))
        span = large[eye]-small[eye]
        if small[eye] < 10 or large[eye] > 60 or span < max(3, small[eye]*.1):
            raise ValueError("GuideYourPupilsChangedSizeTooLittleDimTheRoomLights")
        for cycle, c in enumerate(masks):
            diameter = eyes[c, eye, 0]
            low, high = np.percentile(diameter, (5, 95))
            response[cycle, eye] = np.corrcoef(levels[c], diameter)[0, 1]
            if abs(low-small[eye]) > .35*span or abs(high-large[eye]) > .35*span:
                raise ValueError("GuideTheTwoRoundsDidnTMatchKeepYourHeadStill")
            if not response[cycle, eye] < -.5:
                raise ValueError("GuidePupilSizeDidnTFollowTheScreenBrightnessKeepLooking")
        centers[eye] = np.median(eyes[valid, eye, 1:3], axis=0)
        if np.percentile(np.linalg.norm(eyes[valid, eye, 1:3]-centers[eye], axis=1), 90) > 15:
            raise ValueError("GuideKeepLookingAtTheCircleTheWholeTimeThenTry")
        ratios[eye] = np.median(eyes[valid, eye, 3])
    return {"format": "qpro-relative-pupil-v2", "sourceEyes": selected,
            "small": small.tolist(), "large": large.tolist(), "centers": centers.tolist(), "ratios": ratios.tolist(),
            "response": response.tolist(), "samplesPerCycle": counts[:, selected].min(axis=1).tolist(),
            "samplesPerEye": counts.tolist(), "validation": "independent-brightness-round"}


def read_calibration(path):
    profile = json.loads(Path(path).read_text())
    if profile.get("format") == "qpro-relative-pupil-v2" and "sourceEyes" not in profile:
        raise ValueError("GuideInvalidRelativePupilCalibration")
    source_eyes(profile)
    small, large = np.asarray(profile["small"]), np.asarray(profile["large"])
    centers, ratios = np.asarray(profile["centers"]), np.asarray(profile["ratios"])
    if (profile.get("format") not in ("qpro-relative-pupil-v1", "qpro-relative-pupil-v2") or small.shape != (2,) or large.shape != (2,)
            or centers.shape != (2, 2) or ratios.shape != (2,)
            or not all(np.isfinite(v).all() for v in (small, large, centers, ratios))
            or np.any((small < 10) | (large > 60) | (large-small < np.maximum(3, small*.1)))
            or np.any((centers < 8) | (centers > 392)) or np.any((ratios < .45) | (ratios > 1))):
        raise ValueError("GuideInvalidRelativePupilCalibration")
    return profile


def relative_values(pupils, profile):
    if profile is None:
        return None
    small, large = np.asarray(profile["small"]), np.asarray(profile["large"])
    span = large-small
    d = np.array([np.nan if p is None else p.diameter_px for p in pupils], dtype=float)
    d[[i for i in range(2) if i not in source_eyes(profile)]] = np.nan
    values = np.where((d >= small - .35 * span) & (d <= large + .35 * span), (d - small) / span, np.nan)
    if np.isnan(values).all():
        return None
    return np.clip(np.where(np.isnan(values), np.nanmean(values), values), 0, 1)


class PupilDilation:
    def __init__(self, path="calibration/qpro-pupil-dilation.json", enabled=False, *, asynchronous=False):
        self.path = Path(path)
        self.enabled = enabled
        self.profile = None
        self.message = ""
        if self.path.exists():
            try:
                self.profile = read_calibration(self.path)
            except (ValueError, KeyError, TypeError, OSError) as error:
                self.message = {"key": "GuideCalibrationUnavailableReason", "args": [str(error)]}
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.phase = None
        self.level = 0.
        self.result = "idle"
        self.samples = []
        self.started = 0.0
        self.last_detection = -1.0
        self.last_valid = -1.0
        self.last_pair = 0.
        self.last_eyes = None
        self.filtered = None
        self.last_pupils = [0., 0.]
        self.last_pupil_time = [-1e9, -1e9]
        self.detected_pupils = [None, None]
        self._executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="pupil-detection") if asynchronous else None
        self._pending = None
        self._closed = False
        self.send(False)

    def send(self, valid, values=(.5, .5)):
        if self.enabled:
            self.socket.sendto(PACKET.pack(b"QPPD", 1, int(valid), 0, *values), ("127.0.0.1", 27275))

    def calibrate(self, selected=(0, 1)):
        if self._pending is not None:
            self._pending.result()
            self._pending = None
        self.phase = 0
        self.selected = source_eyes({"sourceEyes": list(selected)})
        self.paused = False
        self.paused_elapsed = 0.
        self.level = 0.
        self.result = "running"
        self.samples = []
        self.started = self.last_pair = time.monotonic()
        self.profile = None
        self.filtered = None
        self.last_valid = -1
        self.message = "GuideLookAtTheCircleKeepYourHeadStillAndBlink"
        self.send(False)

    def hold(self, paused, now):
        if self._pending is not None:
            self._pending.result()
            self._pending = None
        if self.phase is None or self.paused == paused:
            return
        self.paused = paused
        if paused:
            self.paused_elapsed = max(0., now-self.started)
        self.level = 0.
        if not paused:
            self.samples = []
            self.started = self.last_pair = now
            self.phase = 0
            self.last_pupil_time = [-1e9, -1e9]

    def update(self, strip, *, preview=False):
        if self._closed:
            return
        if self._pending is not None:
            if not self._pending.done():
                return
            self._pending.result()
            self._pending = None
        if not (self.enabled or preview or self.phase is not None):
            return
        now = time.monotonic()
        if now-self.last_detection < .125:
            return
        self.last_detection = now
        if self._executor is not None:
            self._pending = self._executor.submit(self._update, strip)
        else:
            self._update(strip)

    def _update(self, strip):
        now = time.monotonic()
        eyes = strip[:, :800]
        fresh = self.last_eyes is None or not np.array_equal(eyes, self.last_eyes)
        self.last_eyes = eyes
        pupils = []
        for i in range(2):
            if not fresh:
                pupils.append(None)
                continue
            options = {}
            if self.profile is not None:
                small, large = self.profile["small"][i], self.profile["large"][i]
                options = {"diameter_range": (small-(large-small)*.35, large+(large-small)*.35)}
            pupil = detect_pupil(strip[:, i*400:(i+1)*400], **options)
            pupils.append(pupil)
        self.update_measurements(pupils, now=now)

    def update_measurements(self, measurements, *, now=None):
        """Apply the same calibration and continuity checks to native detector results."""
        if self._closed:
            return
        now = time.monotonic() if now is None else now
        pupils = []
        for i, pupil in enumerate(measurements):
            if pupil is not None:
                last, seen = self.last_pupils[i], self.last_pupil_time[i]
                self.last_pupils[i], self.last_pupil_time[i] = pupil.diameter_px, now
                if now-seen > .25 or abs(pupil.diameter_px-last) > max(3, last*.16):
                    pupil = None
            pupils.append(pupil)
        self.detected_pupils = pupils
        if self.phase is not None:
            if self.paused:
                self.send(False)
                return
            elapsed = now-self.started
            self.phase = min(2, int(max(0, elapsed-ADAPT)//CYCLE)+(elapsed >= ADAPT))
            self.level = stimulus(elapsed)
            if any(pupils[i] is not None for i in self.selected):
                self.last_pair = now
                if elapsed >= ADAPT-15:
                    self.samples.append((elapsed, [[p.diameter_px, *p.ellipse[0], min(p.ellipse[1])/max(p.ellipse[1])] if p else [np.nan]*4
                                                   for p in pupils]))
            self.message = ("GuideYourPupilsArenTVisibleOpenYourEyesNaturallyAnd"
                            if now-self.last_pair > 2 else
                            "GuideRelaxAndLookAtTheCircleTheScreenStaysDark" if elapsed < ADAPT else
                            "GuideKeepLookingAtTheCircleAsTheScreenFadesBlink")
            if elapsed >= DURATION:
                self.phase = None
                self.level = 0.
                try:
                    profile = fit_calibration(self.samples, self.selected)
                    self.path.parent.mkdir(parents=True, exist_ok=True)
                    pending = self.path.with_suffix(".tmp")
                    pending.write_text(json.dumps(profile, indent=2))
                    pending.replace(self.path)
                    self.profile = profile
                    self.result = "passed"
                    self.message = "GuideCalibrationPassedAndSaved"
                    print("PUPIL_CALIBRATION_SAVED " + str(self.path.resolve()), flush=True)
                except (ValueError, OSError) as error:
                    self.result = "failed"
                    self.message = str(error)
                    print("PUPIL_CALIBRATION_FAILED " + str(error), flush=True)
        values = relative_values(pupils, self.profile)
        if values is not None:
            dt = now-self.last_valid
            self.filtered = values if self.filtered is None or dt > HOLD else self.filtered + (1-np.exp(-dt/.25))*(values-self.filtered)
            self.last_valid = now
        if self.filtered is not None and now-self.last_valid <= HOLD:
            self.send(True, self.filtered)
        else:
            self.filtered = None
            self.send(False)

    def close(self):
        if self._closed:
            return
        self._closed = True
        try:
            if self._executor is not None:
                self._executor.shutdown(wait=True, cancel_futures=True)
            self.send(False)
        finally:
            self.socket.close()
