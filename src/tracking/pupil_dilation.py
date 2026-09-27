"""Calibrated relative pupil size, not a physical millimetre measurement."""
import json
import socket
import struct
import time
import zlib
from pathlib import Path

import cv2
import numpy as np

from pupil_preview import detect_pupil

PACKET = struct.Struct("<4sBBHff")
PHASES = [("Bright", 12, 5), ("Dim", 16, 8), ("Check bright", 12, 5), ("Check dim", 16, 8)]


def fit_calibration(phases):
    if len(phases) != 4 or any(len(p) < 20 for p in phases):
        raise ValueError("Need 20 clear paired-eye readings in each phase")
    arrays = [np.asarray(p, dtype=float) for p in phases]
    if any(a.shape[1:] != (2, 4) or not np.isfinite(a).all() for a in arrays):
        raise ValueError("Invalid pupil calibration samples")
    small, large = [np.median(a[:, :, 0], axis=0) for a in arrays[:2]]
    span = large-small
    if np.any((small < 10) | (large > 60) | (span < np.maximum(3, small*.1))):
        raise ValueError("Pupil response too small; enlarge the window and repeat")
    validation = [(np.median(a[:, :, 0], axis=0)-small)/span for a in arrays[2:]]
    if np.any(np.abs(validation[0]) > .35) or np.any(np.abs(validation[1]-1) > .35):
        raise ValueError("Bright/dim response did not repeat reliably")
    all_samples = np.concatenate(arrays)
    centers = np.median(all_samples[:, :, 1:3], axis=0)
    if np.any(np.percentile(np.linalg.norm(all_samples[:, :, 1:3]-centers, axis=2), 90, axis=0) > 15):
        raise ValueError("Keep looking at the centre cross throughout calibration")
    return {"format": "qpro-relative-pupil-v1", "small": small.tolist(), "large": large.tolist(),
            "centers": centers.tolist(), "ratios": np.median(all_samples[:, :, 3], axis=0).tolist(),
            "checkBright": validation[0].tolist(), "checkDim": validation[1].tolist(),
            "samplesPerPhase": list(map(len, phases))}


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
    if profile is None or any(p is None for p in pupils):
        return None
    centers = np.asarray([p.ellipse[0] for p in pupils])
    ratios = np.asarray([min(p.ellipse[1])/max(p.ellipse[1]) for p in pupils])
    diameter = np.asarray([p.diameter_px for p in pupils])
    small, large = np.asarray(profile["small"]), np.asarray(profile["large"])
    span = large-small
    if (np.any(np.linalg.norm(centers-profile["centers"], axis=1) > 25)
            or np.any(np.abs(ratios-profile["ratios"]) > .18)
            or np.any((diameter < small-.12*span) | (diameter > large+.12*span))):
        return None
    return np.clip((diameter-small)/span, 0, 1)


class PupilDilation:
    def __init__(self, path="calibration/qpro-pupil-dilation.json", enabled=False, *, render=True):
        self.path = Path(path)
        self.enabled = enabled
        self.render = render
        self.profile = None
        self.message = "Maximize this window in Virtual Desktop. Press C to calibrate (56 seconds)."
        if self.path.exists():
            try:
                self.profile = read_calibration(self.path)
                self.message = "Calibration loaded. C recalibrates after changing headset fit."
            except (ValueError, KeyError, TypeError, OSError) as error:
                self.message = f"Calibration unavailable: {error}. Press C."
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.phase = None
        self.result = "idle"
        self.samples = []
        self.started = 0.0
        self.last_detection = -1.0
        self.last_valid = -1.0
        self.last_fingerprint = None
        self.filtered = None
        self.last_pupils = None
        self.last_pupil_time = 0.0
        self.image = np.zeros((800, 1200, 3), np.uint8)
        self.send(False)

    def send(self, valid, values=(.5, .5)):
        if self.enabled:
            self.socket.sendto(PACKET.pack(b"QPPD", 1, int(valid), 0, *values), ("127.0.0.1", 27279))

    def handle_key(self, key):
        if key.lower() == "c":
            self.phase = 0
            self.result = "running"
            self.samples = [[] for _ in PHASES]
            self.started = time.monotonic()
            self.profile = None
            self.filtered = None
            self.last_valid = -1
            self.message = "Look at the centre cross. Keep your head and the window still; blink normally."
            self.send(False)

    def update(self, strip):
        if strip.shape != (400, 2000):
            raise ValueError("Pupil dilation requires all five cameras")
        now = time.monotonic()
        if now-self.last_detection < .125:
            return self.image
        self.last_detection = now
        fingerprint = zlib.crc32(strip[:, :800].tobytes())
        fresh = fingerprint != self.last_fingerprint
        self.last_fingerprint = fingerprint
        pupils = []
        for i in range(2):
            options = {}
            if self.profile is not None:
                small, large = self.profile["small"][i], self.profile["large"][i]
                margin = (large-small)*.12
                options = {"center": self.profile["centers"][i], "diameter_range": (small-margin, large+margin)}
            pupils.append(detect_pupil(strip[:, i*400:(i+1)*400], **options) if fresh else None)
        if all(p is not None for p in pupils):
            diameters = np.asarray([p.diameter_px for p in pupils])
            if (self.last_pupils is not None and now-self.last_pupil_time < .4
                    and np.any(np.abs(diameters-self.last_pupils) > np.maximum(3, self.last_pupils*.16))):
                pupils = [None, None]
            else:
                self.last_pupils, self.last_pupil_time = diameters, now
        self.detected_pupils = pupils
        phase = self.phase
        if phase is not None:
            elapsed = now-self.started
            name, duration, settle = PHASES[phase]
            if settle <= elapsed < duration and all(p is not None for p in pupils):
                self.samples[phase].append([[p.diameter_px, *p.ellipse[0], min(p.ellipse[1])/max(p.ellipse[1])]
                                            for p in pupils])
            if elapsed >= duration:
                self.phase += 1
                self.started = now
                if self.phase == len(PHASES):
                    self.phase = None
                    try:
                        profile = fit_calibration(self.samples)
                        self.path.parent.mkdir(parents=True, exist_ok=True)
                        pending = self.path.with_suffix(".tmp")
                        pending.write_text(json.dumps(profile, indent=2))
                        pending.replace(self.path)
                        self.profile = profile
                        self.result = "passed"
                        self.message = "Calibration passed and saved. Relative dilation " + ("OUTPUT ON." if self.enabled else "preview only.")
                        print("PUPIL_CALIBRATION_SAVED " + str(self.path.resolve()), flush=True)
                    except (ValueError, OSError) as error:
                        self.result = "failed"
                        self.message = str(error) + ". Press C to retry."
                        print("PUPIL_CALIBRATION_FAILED " + str(error), flush=True)
        values = relative_values(pupils, self.profile)
        if values is not None:
            dt = now-self.last_valid
            self.filtered = values if self.filtered is None or dt > .3 else self.filtered + (1-np.exp(-dt/.25))*(values-self.filtered)
            self.last_valid = now
            self.send(True, self.filtered)
        elif now-self.last_valid > .3:
            self.filtered = None
            self.send(False)
        if not self.render and self.phase is None:
            return self.image
        bright = self.phase is not None and self.phase % 2 == 0
        self.image = np.full((800, 1200, 3), 235 if bright else 18, np.uint8)
        color = (25, 25, 25) if bright else (220, 220, 220)
        def text(message, y, size=.6):
            cv2.putText(self.image, message, (25, y), cv2.FONT_HERSHEY_SIMPLEX, size, color, 1, cv2.LINE_AA)
        text("Quest Pro relative pupil dilation", 35, .8)
        text(self.message[:125], 75, .52)
        if self.phase is not None:
            name, duration, _ = PHASES[self.phase]
            text(f"{self.phase+1}/4: {name} - {max(0, duration-(now-self.started)):.0f}s. Keep looking at +", 115)
        else:
            text("C: calibrate    Q: stop tracking    Relative size only; not millimetres", 115, .55)
            if self.filtered is not None:
                text(f"Left {self.filtered[0]:.2f}    Right {self.filtered[1]:.2f}    " +
                     ("OUTPUT ON" if self.enabled else "PREVIEW ONLY"), 160)
            else:
                text("Awaiting calibration / two clear pupils; output is neutral.", 160)
        cv2.drawMarker(self.image, (600, 350), color, cv2.MARKER_CROSS, 25, 2)
        for side, pupil in enumerate(pupils):
            panel = cv2.cvtColor(strip[:, side*400:(side+1)*400], cv2.COLOR_GRAY2BGR)
            if pupil:
                cv2.ellipse(panel, pupil.ellipse, (0, 220, 100), 2)
            self.image[535:775, 350+side*250:590+side*250] = cv2.resize(panel, (240, 240))
        return self.image

    def close(self):
        try:
            self.send(False)
        finally:
            self.socket.close()
