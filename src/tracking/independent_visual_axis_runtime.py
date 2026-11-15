#!/usr/bin/env python3
"""Apply the saved independent visual-axis calibration and feed per-eye gaze to the VRCFT bridge."""

from __future__ import annotations

import argparse
import json
import math
import queue
import socket
import struct
import threading
from pathlib import Path

import numpy as np

from eye_signal_filter import IndependentEyeFilter
from eye_trace import RawEyeSample
from eye_session import IndependentEyeSession
from headset import stop_event


PACKET_MAGIC = b"QPGE"
PACKET_VERSION = 1
PACKET = struct.Struct("<4sBBHffff")
GAZE_PORT = 27275
CONTROL_PORT = 27277


def load_calibration(path: str | Path) -> list[tuple[int, np.ndarray]]:
    """The physical left and right eye's (trace tag, coefficients (3, 2)), checked once here instead of per sample."""
    payload = json.loads(Path(path).read_text(encoding="utf-8"))
    if payload.get("format") != "qpro-independent-personalized-visual-axis-v2":
        raise ValueError("This is not an independent visual-axis calibration")
    if not payload.get("quality_gate", {}).get("gaze_pass", False):
        raise ValueError("The saved calibration did not pass its absolute-gaze gate")
    tag_mapping = payload.get("detector_tag_mapping", {})
    eyes = []
    for tag, eye in enumerate(("left", "right")):
        source = tag_mapping.get(f"physical_{eye}", f"trace_tag_{tag}")
        if source not in ("trace_tag_0", "trace_tag_1"):
            raise ValueError("The calibration has an unsupported detector-tag mapping")
        coefficients = np.asarray(payload[eye]["coefficients"], dtype=np.float64)
        if coefficients.shape != (3, 2) or not np.all(np.isfinite(coefficients)):
            raise ValueError(f"The {eye} calibration coefficients are invalid")
        eyes.append((int(source[-1]), coefficients))
    return eyes


def calibrated_angles(
    calibration: list[tuple[int, np.ndarray]], sample: RawEyeSample
) -> tuple[np.ndarray, np.ndarray]:
    """Each eye's affine fit [1, yaw, pitch] @ coefficients, on the trace tag the calibration assigned to it."""
    sources = (sample.left_angles, sample.right_angles)
    return tuple(np.r_[1.0, sources[tag]] @ coefficients for tag, coefficients in calibration)


def amplify_vergence(
    left: np.ndarray, right: np.ndarray, gain: float
) -> tuple[np.ndarray, np.ndarray]:
    """Scale each eye's deviation from the mean gaze by `gain`: keeps where you
    look, exaggerates how much the eyes converge/diverge."""
    left = np.asarray(left, dtype=np.float64)
    right = np.asarray(right, dtype=np.float64)
    mean = (left + right) / 2.0
    return mean + (left - mean) * gain, mean + (right - mean) * gain


def encode_packet(left_deg: np.ndarray, right_deg: np.ndarray) -> bytes:
    """Calibrated yaw/pitch degrees as VRCFT gaze radians. The node-18 calibration already resolves the headset
    camera convention, so yaw keeps its calibrated sign; VRCFT's left eye is the calibration's right and vice versa."""
    left_x, left_y = map(math.radians, map(float, right_deg))
    right_x, right_y = map(math.radians, map(float, left_deg))
    return PACKET.pack(PACKET_MAGIC, PACKET_VERSION, 3, 0, left_x, left_y, right_x, right_y)


class VergenceControl:
    """Live vergence gain over a localhost UDP control channel.

    A daemon thread blocks on recv and updates the gain only when the hub sends a
    new value: event-driven, no polling. The render loop just reads `.gain` (a
    plain float read). If the port is unavailable the initial gain is kept.
    """

    def __init__(self, initial: float, port: int = CONTROL_PORT, enabled: bool = True) -> None:
        self.gain = float(initial)
        self.enabled = enabled
        self._socket: socket.socket | None = None
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.bind(("127.0.0.1", port))
            self._socket = sock
            threading.Thread(target=self._listen, daemon=True).start()
        except OSError:
            raise RuntimeError('EYE_SWAP_BUSY')

    def _listen(self) -> None:
        assert self._socket is not None
        while True:
            try:
                data, _ = self._socket.recvfrom(64)
            except OSError:
                return
            try:
                command = data.decode("ascii", "strict").strip()
                if command in ('eyes:0', 'eyes:1'):
                    self.enabled = command == 'eyes:1'
                    continue
                value = float(command)
            except (ValueError, UnicodeError):
                continue
            if 0.0 <= value <= 3.0:
                self.gain = value

    def close(self) -> None:
        if self._socket is not None:
            self._socket.close()
            self._socket = None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--calibration", required=True)
    parser.add_argument("--vergence-gain", type=float, default=1.0,
                        help="scale each eye's deviation from mean gaze (1.0 = as calibrated)")
    parser.add_argument('--enabled', choices=('0','1'), default='1')
    arguments = parser.parse_args()
    stopped = stop_event()
    calibration = None
    vergence = VergenceControl(arguments.vergence_gain, enabled=arguments.enabled == '1')
    print('QFT_EYES_CONTROL_READY', flush=True)
    print(f"vergence gain = {arguments.vergence_gain:.2f} (live on udp/{CONTROL_PORT})", flush=True)
    reader = None
    attempted = False
    eye_filter = IndependentEyeFilter(2)
    output = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        while not stopped.is_set():
            if not vergence.enabled:
                if attempted and reader is None:
                    print('QFT_EYES_STANDARD', flush=True)
                attempted = False
            if vergence.enabled and reader is None and not attempted:
                attempted = True
                calibration = load_calibration(arguments.calibration)
                reader = IndependentEyeSession()
                try:
                    reader.start()
                except (RuntimeError, ValueError) as error:
                    if str(error) != 'EYE_SWAP_UNSUPPORTED':
                        raise
                    reader = None
                    print('QFT_EYES_UNAVAILABLE EYE_SWAP_UNSUPPORTED', flush=True)
                    continue
                print('QFT_EYES_ACTIVE', flush=True)
                eye_filter = IndependentEyeFilter(2)
            elif not vergence.enabled and reader is not None:
                reader.close()
                reader = None
                print('QFT_EYES_STANDARD', flush=True)
            if reader is None:
                stopped.wait(.05)
                continue
            if not reader.errors.empty():
                print("QFT_ERROR: " + reader.errors.get_nowait(), flush=True)
                return 1
            try:
                sample = reader.samples.get(timeout=0.1)
            except queue.Empty:
                continue
            left, right = calibrated_angles(calibration, sample)
            left, right = eye_filter.update(left, right, sample.pc_monotonic_ns / 1_000_000_000.0)
            if vergence.gain != 1.0:
                left, right = amplify_vergence(left, right, vergence.gain)
            output.sendto(encode_packet(left, right), ("127.0.0.1", GAZE_PORT))
        return 0
    finally:
        vergence.close()
        output.close()
        if reader is not None:
            reader.close()


if __name__ == "__main__":
    raise SystemExit(main())
