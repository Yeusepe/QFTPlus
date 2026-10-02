"""Calibrated relative pupil size, not a physical millimetre measurement."""
import json
import socket
import struct
import time
import zlib
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
STAGES = ["Let your eyes adjust", "Look at the circle", "Look at the circle"]


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


def fit_calibration(samples):
    times = np.asarray([s[0] for s in samples], dtype=float)
    eyes = np.asarray([s[1] for s in samples], dtype=float)
    cycles = [times < ADAPT+CYCLE, times >= ADAPT+CYCLE]
    if any(c.sum() < 80 for c in cycles):
        raise ValueError("Your pupils were hidden too often. Adjust the headset so it sits level, then try again")
    if eyes.ndim != 3 or eyes.shape[1:] != (2, 4) or not np.isfinite(eyes).all() or not np.isfinite(times).all():
        raise ValueError("Invalid pupil calibration samples")
    diameter = eyes[:, :, 0]
    small, large = np.percentile(diameter, (5, 95), axis=0)
    span = large-small
    if np.any((small < 10) | (large > 60) | (span < np.maximum(3, small*.1))):
        raise ValueError("Your pupils changed size too little. Dim the room lights, then try again")
    levels = np.asarray([stimulus(t-LAG) for t in times])
    response = []
    for c in cycles:
        low, high = np.percentile(diameter[c], (5, 95), axis=0)
        response.append([np.corrcoef(levels[c], diameter[c, i])[0, 1] for i in range(2)])
        if np.any(np.abs(low-small) > .35*span) or np.any(np.abs(high-large) > .35*span):
            raise ValueError("The two rounds didn't match. Keep your head still and look at the circle, then try again")
    if not np.all(np.asarray(response) < -.5):
        raise ValueError("Pupil size didn't follow the screen brightness. Keep looking at the circle, then try again")
    centers = np.median(eyes[:, :, 1:3], axis=0)
    if np.any(np.percentile(np.linalg.norm(eyes[:, :, 1:3]-centers, axis=2), 90, axis=0) > 15):
        raise ValueError("Keep looking at the circle the whole time, then try again")
    return {"format": "qpro-relative-pupil-v1", "small": small.tolist(), "large": large.tolist(),
            "centers": centers.tolist(), "ratios": np.median(eyes[:, :, 3], axis=0).tolist(),
            "response": response, "samplesPerCycle": [int(c.sum()) for c in cycles]}


def read_calibration(path):
    profile = json.loads(Path(path).read_text())
    small, large = np.asarray(profile["small"]), np.asarray(profile["large"])
    centers, ratios = np.asarray(profile["centers"]), np.asarray(profile["ratios"])
    if (profile.get("format") != "qpro-relative-pupil-v1" or small.shape != (2,) or large.shape != (2,)
            or centers.shape != (2, 2) or ratios.shape != (2,)
            or not all(np.isfinite(v).all() for v in (small, large, centers, ratios))
            or np.any((small < 10) | (large > 60) | (large-small < np.maximum(3, small*.1)))
            or np.any((centers < 8) | (centers > 392)) or np.any((ratios < .45) | (ratios > 1))):
        raise ValueError("Invalid relative-pupil calibration")
    return profile


def relative_values(pupils, profile):
    if profile is None:
        return None
    small, large = np.asarray(profile["small"]), np.asarray(profile["large"])
    span = large-small
    values = np.full(2, np.nan)
    for i, pupil in enumerate(pupils):
        if pupil is not None and small[i]-.35*span[i] <= pupil.diameter_px <= large[i]+.35*span[i]:
            values[i] = (pupil.diameter_px-small[i])/span[i]
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
                self.message = f"Calibration unavailable: {error}."
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.phase = None
        self.level = 0.
        self.result = "idle"
        self.samples = []
        self.started = 0.0
        self.last_detection = -1.0
        self.last_valid = -1.0
        self.last_pair = 0.
        self.last_fingerprint = None
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
            self.socket.sendto(PACKET.pack(b"QPPD", 1, int(valid), 0, *values), ("127.0.0.1", 27279))

    def calibrate(self):
        if self._pending is not None:
            self._pending.result()
            self._pending = None
        self.phase = 0
        self.level = 0.
        self.result = "running"
        self.samples = []
        self.started = self.last_pair = time.monotonic()
        self.profile = None
        self.filtered = None
        self.last_valid = -1
        self.message = "Look at the circle. Keep your head still and blink normally."
        self.send(False)

    def update(self, strip, *, preview=False):
        if strip.shape != (400, 2000):
            raise ValueError("Pupil dilation requires all five cameras")
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
        if self._executor is not None and self.phase is None:
            self._pending = self._executor.submit(self._update, strip.copy())
        else:
            self._update(strip)

    def _update(self, strip):
        now = time.monotonic()
        fingerprint = zlib.crc32(strip[:, :800].tobytes())
        fresh = fingerprint != self.last_fingerprint
        self.last_fingerprint = fingerprint
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
            if pupil is not None:
                last, seen = self.last_pupils[i], self.last_pupil_time[i]
                self.last_pupils[i], self.last_pupil_time[i] = pupil.diameter_px, now
                if now-seen > .25 or abs(pupil.diameter_px-last) > max(3, last*.16):
                    pupil = None
            pupils.append(pupil)
        self.detected_pupils = pupils
        both = all(p is not None for p in pupils)
        if self.phase is not None:
            elapsed = now-self.started
            self.phase = min(2, int(max(0, elapsed-ADAPT)//CYCLE)+(elapsed >= ADAPT))
            self.level = stimulus(elapsed)
            if both:
                self.last_pair = now
                if elapsed >= ADAPT-15:
                    self.samples.append((elapsed, [[p.diameter_px, *p.ellipse[0], min(p.ellipse[1])/max(p.ellipse[1])]
                                                   for p in pupils]))
            self.message = ("Your pupils aren't visible. Open your eyes naturally and make sure the headset sits level."
                            if now-self.last_pair > 2 else
                            "Relax and look at the circle. The screen stays dark while your eyes adjust." if elapsed < ADAPT else
                            "Keep looking at the circle as the screen fades. Blink normally.")
            if elapsed >= DURATION:
                self.phase = None
                self.level = 0.
                try:
                    profile = fit_calibration(self.samples)
                    self.path.parent.mkdir(parents=True, exist_ok=True)
                    pending = self.path.with_suffix(".tmp")
                    pending.write_text(json.dumps(profile, indent=2))
                    pending.replace(self.path)
                    self.profile = profile
                    self.result = "passed"
                    self.message = "Calibration passed and saved."
                    print("PUPIL_CALIBRATION_SAVED " + str(self.path.resolve()), flush=True)
                except (ValueError, OSError) as error:
                    self.result = "failed"
                    self.message = str(error) + "."
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
