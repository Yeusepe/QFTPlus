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
from typing import Any

import numpy as np

from eye_signal_filter import IndependentEyeFilter
from eye_trace import RawEyeSample, RawTraceEyeReader
from headset import stop_event


PACKET_MAGIC = b"QPGE"
PACKET_VERSION = 1
PACKET_FORMAT = "<4sBBHffff"
GAZE_PORT = 27275
CONTROL_PORT = 27277


def load_calibration(path: str | Path) -> dict[str, Any]:
    payload = json.loads(Path(path).read_text(encoding="utf-8"))
    if payload.get("format") != "qpro-independent-personalized-visual-axis-v2":
        raise ValueError("This is not an independent visual-axis calibration")
    if not payload.get("quality_gate", {}).get("gaze_pass", False):
        raise ValueError("The saved calibration did not pass its absolute-gaze gate")
    for eye in ("left", "right"):
        coefficients = np.asarray(payload[eye]["coefficients"], dtype=np.float64)
        if coefficients.shape != (3, 2) or not np.all(np.isfinite(coefficients)):
            raise ValueError(f"The {eye} calibration coefficients are invalid")
    return payload


def calibrated_angles(
    calibration: dict[str, Any], sample: RawEyeSample
) -> tuple[np.ndarray, np.ndarray]:
    """Each eye's affine fit [1, yaw, pitch] @ coefficients, on the trace tag the calibration assigned to it."""
    tag_mapping = calibration.get("detector_tag_mapping", {})
    sources = {"trace_tag_0": sample.left_angles, "trace_tag_1": sample.right_angles}
    physical = (tag_mapping.get("physical_left", "trace_tag_0"), tag_mapping.get("physical_right", "trace_tag_1"))
    if not set(physical) <= set(sources):
        raise ValueError("The calibration has an unsupported detector-tag mapping")
    return tuple(np.r_[1.0, sources[source]] @ np.asarray(calibration[eye]["coefficients"], dtype=np.float64)
                 for eye, source in zip(("left", "right"), physical))


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
    return struct.pack(PACKET_FORMAT, PACKET_MAGIC, PACKET_VERSION, 3, 0, left_x, left_y, right_x, right_y)


class VergenceControl:
    """Live vergence gain over a localhost UDP control channel.

    A daemon thread blocks on recv and updates the gain only when the hub sends a
    new value: event-driven, no polling. The render loop just reads `.gain` (a
    plain float read). If the port is unavailable the initial gain is kept.
    """

    def __init__(self, initial: float, port: int = CONTROL_PORT) -> None:
        self.gain = float(initial)
        self._socket: socket.socket | None = None
        try:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            sock.bind(("127.0.0.1", port))
            self._socket = sock
            threading.Thread(target=self._listen, daemon=True).start()
        except OSError:
            self._socket = None

    def _listen(self) -> None:
        assert self._socket is not None
        while True:
            try:
                data, _ = self._socket.recvfrom(64)
            except OSError:
                return
            try:
                value = float(data.decode("ascii", "ignore").strip())
            except ValueError:
                continue
            if 0.0 <= value <= 10.0:
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
    arguments = parser.parse_args()
    stopped = stop_event()
    calibration = load_calibration(arguments.calibration)
    vergence = VergenceControl(arguments.vergence_gain)
    print(f"vergence gain = {arguments.vergence_gain:.2f} (live on udp/{CONTROL_PORT})", flush=True)
    reader = RawTraceEyeReader()
    eye_filter = IndependentEyeFilter(2, min_cutoff_hz=4.0, beta=0.15, derivative_cutoff_hz=1.5)
    output = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        reader.start()
        while not stopped.is_set():
            if not reader.errors.empty():
                raise RuntimeError(reader.errors.get_nowait())
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
        reader.close()


if __name__ == "__main__":
    raise SystemExit(main())
