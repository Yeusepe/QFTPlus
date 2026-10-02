#!/usr/bin/env python3
"""Calibrate Meta's personalized, independent local-branch visual axes."""

from __future__ import annotations

import bisect
import codecs
import collections
import json
import socket
import subprocess
import threading
from dataclasses import dataclass
from pathlib import Path
import time
from typing import Any

import numpy as np

def error_metrics(predicted: np.ndarray, expected: np.ndarray) -> dict[str, float]:
    delta = np.asarray(predicted) - np.asarray(expected)
    errors = np.abs(delta)
    angular = np.linalg.norm(delta, axis=1)
    return {
        "yaw_mae_deg": float(np.mean(errors[:, 0])),
        "pitch_mae_deg": float(np.mean(errors[:, 1])),
        "angular_mae_deg": float(np.mean(angular)),
        "angular_p95_deg": float(np.percentile(angular, 95)),
    }


class _JsonObjectStream:
    def __init__(self) -> None:
        self.buffer = ""
        self.decoder = json.JSONDecoder()

    def feed(self, chunk: str) -> list[dict[str, Any]]:
        self.buffer = (self.buffer + chunk).lstrip()
        if len(self.buffer) > 1_048_576:
            raise ValueError("VR overlay sent an oversized packet")
        objects: list[dict[str, Any]] = []
        while self.buffer:
            if not self.buffer.startswith("{"):
                raise ValueError("VR overlay packets must be JSON objects")
            try:
                packet, end = self.decoder.raw_decode(self.buffer)
            except json.JSONDecodeError:
                break
            objects.append(packet)
            self.buffer = self.buffer[end:].lstrip()
        return objects


@dataclass(frozen=True)
class TargetState:
    received_ns: int
    left_pitch: float
    left_yaw: float
    right_pitch: float
    right_yaw: float
    distance: float


def affine_features(values: np.ndarray) -> np.ndarray:
    values = np.asarray(values, dtype=np.float64)
    if values.ndim == 1:
        values = values.reshape(1, 2)
    return np.column_stack([np.ones(len(values), dtype=np.float64), values])


def fit_axis_eye_mapping(samples: list[dict[str, Any]], eye: str) -> dict[str, Any]:
    """Fit a conservative independent affine correction for one visual axis."""
    if eye not in ("left", "right"):
        raise ValueError("eye must be left or right")
    if len(samples) < 120:
        raise ValueError(f"Need at least 120 gaze samples, got {len(samples)}")
    raw = np.asarray([sample[f"{eye}_raw"] for sample in samples], dtype=np.float64)
    target = np.asarray(
        [sample[f"{eye}_target_deg"] for sample in samples], dtype=np.float64
    )
    features = affine_features(raw)
    started_ns = min(int(sample["timestamp_ns"]) for sample in samples)
    blocks = np.asarray(
        [
            (int(sample["timestamp_ns"]) - started_ns) // 1_000_000_000
            for sample in samples
        ],
        dtype=np.int64,
    )
    holdout = blocks % 5 == 4
    if int(np.count_nonzero(holdout)) < 24:
        holdout = np.arange(len(samples)) % 5 == 4
    training = ~holdout
    penalty = np.diag([0.0, 0.002, 0.002])

    def solve(indices: np.ndarray) -> np.ndarray:
        selected = features[indices]
        return np.linalg.solve(
            selected.T @ selected + penalty,
            selected.T @ target[indices],
        )

    coefficients = solve(training)
    training_error = np.linalg.norm(
        features[training] @ coefficients - target[training], axis=1
    )
    median = float(np.median(training_error))
    mad = float(np.median(np.abs(training_error - median)))
    cutoff = max(1.5, median + 4.0 * max(mad, 0.15))
    clean_training = np.flatnonzero(training)[training_error <= cutoff]
    if len(clean_training) >= 96:
        coefficients = solve(clean_training)
    held_prediction = features[holdout] @ coefficients

    all_error = np.linalg.norm(features @ coefficients - target, axis=1)
    all_median = float(np.median(all_error))
    all_mad = float(np.median(np.abs(all_error - all_median)))
    clean = all_error <= max(1.5, all_median + 4.0 * max(all_mad, 0.15))
    if int(np.count_nonzero(clean)) >= 120:
        coefficients = solve(clean)

    prediction = features @ coefficients
    return {
        "model": "independent-yaw-pitch-affine",
        "coefficients": coefficients.tolist(),
        "input_order": ["1", "visual_yaw_deg", "visual_pitch_deg"],
        "output_order": ["target_yaw_deg", "target_pitch_deg"],
        "sample_count": len(samples),
        "retained_count": int(np.count_nonzero(clean)),
        "holdout_count": int(np.count_nonzero(holdout)),
        "held_out": error_metrics(held_prediction, target[holdout]),
        "all_samples": error_metrics(prediction, target),
        "raw_range_deg": {
            "yaw": [float(np.min(raw[:, 0])), float(np.max(raw[:, 0]))],
            "pitch": [float(np.min(raw[:, 1])), float(np.max(raw[:, 1]))],
        },
        "target_range_deg": {
            "yaw": [float(np.min(target[:, 0])), float(np.max(target[:, 0]))],
            "pitch": [float(np.min(target[:, 1])), float(np.max(target[:, 1]))],
        },
    }


def apply_axis_eye_mapping(
    mapping: dict[str, Any], angles: tuple[float, float] | list[float]
) -> tuple[float, float]:
    coefficients = np.asarray(mapping["coefficients"], dtype=np.float64)
    prediction = affine_features(np.asarray(angles, dtype=np.float64))[0] @ coefficients
    return float(prediction[0]), float(prediction[1])


def evaluate_independent_convergence(
    samples: list[dict[str, Any]],
    left_mapping: dict[str, Any],
    right_mapping: dict[str, Any],
) -> dict[str, Any]:
    """Evaluate vergence derived after two isolated per-eye mappings."""
    result: dict[str, Any] = {}
    for phase in ("gaze", "convergence"):
        selected = [sample for sample in samples if sample["phase"] == phase]
        if len(selected) < 60:
            raise ValueError(f"Need at least 60 {phase} samples, got {len(selected)}")
        mapped_left = np.asarray(
            [apply_axis_eye_mapping(left_mapping, sample["left_raw"]) for sample in selected]
        )
        mapped_right = np.asarray(
            [apply_axis_eye_mapping(right_mapping, sample["right_raw"]) for sample in selected]
        )
        predicted = mapped_right[:, 0] - mapped_left[:, 0]
        expected = np.asarray(
            [
                sample["right_target_deg"][0] - sample["left_target_deg"][0]
                for sample in selected
            ],
            dtype=np.float64,
        )
        raw = np.asarray(
            [sample["right_raw"][0] - sample["left_raw"][0] for sample in selected],
            dtype=np.float64,
        )
        error = np.abs(predicted - expected)
        correlation = float(np.corrcoef(predicted, expected)[0, 1])
        if not np.isfinite(correlation):
            correlation = 0.0
        result[phase] = {
            "sample_count": len(selected),
            "mae_deg": float(np.mean(error)),
            "p95_deg": float(np.percentile(error, 95)),
            "correlation": correlation,
            "raw_disparity_range_deg": [float(np.min(raw)), float(np.max(raw))],
            "mapped_disparity_range_deg": [
                float(np.min(predicted)), float(np.max(predicted))
            ],
            "target_disparity_range_deg": [
                float(np.min(expected)), float(np.max(expected))
            ],
            "distance_range_m": [
                float(min(sample["distance_m"] for sample in selected)),
                float(max(sample["distance_m"] for sample in selected)),
            ],
        }
    return result


class VisualAxisCalibrationController:
    """Collect and fit the patched model's post-personalization eye axes."""

    def __init__(
        self,
        overlay_executable: str | Path,
        output_path: str | Path,
        gaze_seconds: int = 60,
        convergence_seconds: int = 80,
        port: int = 2425,
        openvr: bool = True,
    ) -> None:
        self.overlay_executable = Path(overlay_executable).resolve()
        if not self.overlay_executable.is_file():
            raise FileNotFoundError(
                f"BabbleCalibration executable not found: {self.overlay_executable}"
            )
        self.output_path = Path(output_path).resolve()
        self.gaze_seconds = gaze_seconds
        self.convergence_seconds = convergence_seconds
        self.port = port
        self.openvr = openvr
        self.phase = "starting"
        self.message = "Starting the in-VR calibrator..."
        self.samples: list[dict[str, Any]] = []
        self.latest_target: TargetState | None = None
        self.target_history: collections.deque[TargetState] = collections.deque(
            maxlen=4096
        )
        self.result: dict[str, Any] | None = None
        self._server: socket.socket | None = None
        self._client: socket.socket | None = None
        self._process: subprocess.Popen[bytes] | None = None
        self._process_output = None
        self._thread: threading.Thread | None = None
        self._lock = threading.RLock()
        self._stopping = False

    def start(self) -> None:
        self._server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self._server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self._server.bind(("127.0.0.1", self.port))
        self._server.listen(1)
        self._server.settimeout(20.0)
        arguments = [str(self.overlay_executable)]
        arguments.append("--use-openvr" if self.openvr else "--use-debug")
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        self.output_path.parent.mkdir(parents=True, exist_ok=True)
        overlay_log = self.output_path.parent / "babble-calibration-overlay.log"
        self._process_output = overlay_log.open("w", encoding="utf-8")
        self._process = subprocess.Popen(
            arguments,
            cwd=str(self.overlay_executable.parent),
            creationflags=creation_flags,
            stdout=self._process_output,
            stderr=subprocess.STDOUT,
        )
        print(
            f"BABBLE_OVERLAY_STARTED mode={'openvr' if self.openvr else 'debug'} "
            f"log={overlay_log}"
        )
        self._thread = threading.Thread(
            target=self._connection_worker,
            name="babble-calibration-bridge",
            daemon=True,
        )
        self._thread.start()

    @staticmethod
    def _duration(seconds: int) -> str:
        hours, remainder = divmod(seconds, 3600)
        minutes, secs = divmod(remainder, 60)
        return f"{hours:02d}:{minutes:02d}:{secs:02d}"

    def _send(self, packet_name: str, packet_data: dict[str, Any]) -> None:
        payload = json.dumps(
            {"PacketName": packet_name, "PacketData": packet_data},
            separators=(",", ":"),
        ).encode("utf-8")
        with self._lock:
            if self._client is None:
                raise ConnectionError("BabbleCalibration is not connected")
            self._client.sendall(payload)

    def _send_routine(self, name: str, seconds: int) -> None:
        self._send(
            "RunVariableLenghtRoutinePacket",
            {"RoutineName": name, "Time": self._duration(seconds)},
        )

    def _connection_worker(self) -> None:
        try:
            assert self._server is not None
            client, _address = self._server.accept()
            client.settimeout(1.0)
            with self._lock:
                self._client = client
                self.phase = "initializing_overlay"
                self.message = "VR calibrator connected; waiting for its routine handler..."
            print("BABBLE_OVERLAY_CONNECTED")
            time.sleep(1.25)
            self._send_routine("gazetutorialshort", 3600)
            with self._lock:
                self.phase = "ready_gaze"
                self.message = "In VR: read the gaze tutorial. Press Space when ready."
            print("BABBLE_ROUTINE_READY name=gazetutorialshort key=Space")
            decoder = _JsonObjectStream()
            utf8 = codecs.getincrementaldecoder("utf-8")()
            while not self._stopping:
                try:
                    chunk = client.recv(65536)
                except socket.timeout:
                    continue
                except (ConnectionResetError, ConnectionAbortedError):
                    with self._lock:
                        completed = self.phase == "done"
                    if self._stopping or completed:
                        break
                    raise
                if not chunk:
                    if not self._stopping and self.phase != "done":
                        raise ConnectionError("BabbleCalibration disconnected")
                    break
                for packet in decoder.feed(utf8.decode(chunk)):
                    self._handle_packet(packet)
        except Exception as error:
            with self._lock:
                if not self._stopping and self.phase != "done":
                    self.phase = "error"
                    self.message = f"VR calibration error: {error}"

    def _handle_packet(self, packet: dict[str, Any]) -> None:
        name = packet.get("PacketName")
        data = packet.get("PacketData") or {}
        if name == "HmdPositionalDataPacket":
            target = TargetState(
                received_ns=time.monotonic_ns(),
                left_pitch=float(data.get("LeftEyePitch", 0.0)),
                left_yaw=float(data.get("LeftEyeYaw", 0.0)),
                right_pitch=float(data.get("RightEyePitch", 0.0)),
                right_yaw=float(data.get("RightEyeYaw", 0.0)),
                distance=float(data.get("RoutineDistance", 0.0)),
            )
            with self._lock:
                self.latest_target = target
                self.target_history.append(target)
        elif name == "RoutineFinishedPacket":
            routine = str(data.get("RoutineName", "")).lower()
            if routine == "gaze" and self.phase == "gaze":
                with self._lock:
                    self.phase = "ready_convergence"
                    self.message = (
                        "Gaze capture complete. Read the convergence tutorial; "
                        "press Space when ready."
                    )
                self._send_routine("convergencetutorial", 3600)
            elif routine == "convergence" and self.phase == "convergence":
                self._fit_and_save()

    def handle_key(self, key: str, factory_ready: bool = True) -> None:
        normalized = key.lower()
        with self._lock:
            phase = self.phase
        if normalized == "r" and phase == "ready_gaze":
            self._send_routine("gazetutorialshort", 3600)
            print("BABBLE_ROUTINE_RESENT name=gazetutorialshort")
            return
        if normalized == "r" and phase == "ready_convergence":
            self._send_routine("convergencetutorial", 3600)
            print("BABBLE_ROUTINE_RESENT name=convergencetutorial")
            return
        if normalized not in (" ", "\r", "\n"):
            return
        if phase in ("ready_gaze", "ready_convergence") and not factory_ready:
            with self._lock:
                self.message = (
                    "Waiting for valid changing Meta eye poses. Move your eyes, "
                    "then press Space again."
                )
            print("FACTORY_EYE_PREFLIGHT_WAITING")
            return
        if phase == "ready_gaze":
            with self._lock:
                self.phase = "gaze"
                self.latest_target = None
                self.target_history.clear()
                self.message = "Follow the target naturally while moving your head slowly."
            self._send_routine("gaze", self.gaze_seconds)
            print(f"BABBLE_ROUTINE_STARTED name=gaze seconds={self.gaze_seconds}")
        elif phase == "ready_convergence":
            with self._lock:
                self.phase = "convergence"
                self.latest_target = None
                self.target_history.clear()
                self.message = (
                    "Keep both eyes on the target. Slowly turn/nod your head in a wide figure-eight "
                    "while it moves near and far; let your eyes counter-rotate to hold fixation."
                )
            self._send_routine("convergence", self.convergence_seconds)
            print(
                "BABBLE_ROUTINE_STARTED name=convergence "
                f"seconds={self.convergence_seconds} instruction=slow_head_figure_eight"
            )

    def target_at(
        self, timestamp_ns: int, max_distance_ns: int = 100_000_000
    ) -> TargetState | None:
        """Return/interpolate the VR target at a capture-time timestamp."""
        with self._lock:
            history = list(self.target_history)
        if not history:
            return None
        timestamp_ns = int(timestamp_ns)
        after_index = bisect.bisect_left(history, timestamp_ns, key=lambda target: target.received_ns)
        before = history[after_index - 1] if after_index > 0 else None
        after = history[after_index] if after_index < len(history) else None
        if before is not None and after is not None:
            before_delta = timestamp_ns - before.received_ns
            after_delta = after.received_ns - timestamp_ns
            if before_delta <= max_distance_ns and after_delta <= max_distance_ns:
                span = after.received_ns - before.received_ns
                fraction = 0.0 if span <= 0 else before_delta / span

                def blend(first: float, second: float) -> float:
                    return first + (second - first) * fraction

                return TargetState(
                    received_ns=timestamp_ns,
                    left_pitch=blend(before.left_pitch, after.left_pitch),
                    left_yaw=blend(before.left_yaw, after.left_yaw),
                    right_pitch=blend(before.right_pitch, after.right_pitch),
                    right_yaw=blend(before.right_yaw, after.right_yaw),
                    distance=blend(before.distance, after.distance),
                )
        nearest = min(history, key=lambda target: abs(target.received_ns - timestamp_ns))
        if abs(nearest.received_ns - timestamp_ns) <= max_distance_ns:
            return nearest
        return None

    def close(self) -> None:
        self._stopping = True
        with self._lock:
            client = self._client
            self._client = None
        if client is not None:
            try:
                client.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            client.close()
        if self._server is not None:
            self._server.close()
            self._server = None
        if self._process is not None and self._process.poll() is None:
            self._process.terminate()
            try:
                self._process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self._process.kill()
        self._process = None
        if self._process_output is not None:
            self._process_output.close()
            self._process_output = None

    def add_axis_sample(
        self,
        left_angles: tuple[float, float],
        right_angles: tuple[float, float],
        timestamp_ns: int,
        *,
        kernel_time_s: float | None = None,
    ) -> None:
        with self._lock:
            phase = self.phase
        target = self.target_at(timestamp_ns)
        if phase not in ("gaze", "convergence") or target is None:
            return
        physical_left = right_angles
        physical_right = left_angles
        sample: dict[str, Any] = {
            "timestamp_ns": int(timestamp_ns),
            "phase": phase,
            "distance_m": target.distance,
            "left_raw": [float(physical_left[0]), float(physical_left[1])],
            "right_raw": [float(physical_right[0]), float(physical_right[1])],
            "detector_tag_mapping": {
                "physical_left": "trace_tag_1",
                "physical_right": "trace_tag_0",
            },
            "left_target_deg": [-target.left_yaw, target.left_pitch],
            "right_target_deg": [-target.right_yaw, target.right_pitch],
        }
        if kernel_time_s is not None:
            sample["headset_kernel_time_s"] = float(kernel_time_s)
        with self._lock:
            self.samples.append(sample)

    def _fit_and_save(self) -> None:
        with self._lock:
            self.phase = "fitting"
            self.message = "Fitting isolated affine corrections for each Meta visual axis..."
            samples = list(self.samples)
        try:
            gaze = [sample for sample in samples if sample["phase"] == "gaze"]
            left = fit_axis_eye_mapping(gaze, "left")
            right = fit_axis_eye_mapping(gaze, "right")
            convergence = evaluate_independent_convergence(samples, left, right)
            gaze_pass = bool(
                left["held_out"]["angular_mae_deg"] <= 2.0
                and right["held_out"]["angular_mae_deg"] <= 2.0
                and left["held_out"]["angular_p95_deg"] <= 4.0
                and right["held_out"]["angular_p95_deg"] <= 4.0
            )
            convergence_pass = bool(
                convergence["convergence"]["mae_deg"] <= 1.5
                and convergence["convergence"]["p95_deg"] <= 3.0
                and convergence["convergence"]["correlation"] >= 0.65
            )
            result = {
                "format": "qpro-independent-personalized-visual-axis-v2",
                "created_unix_ns": time.time_ns(),
                "source": (
                    "Seacliff node 18 local per-eye branch, followed by Meta "
                    "VisualAxisDetector personalization"
                ),
                "mapping_constraint": (
                    "left and right affine mappings are fitted independently; "
                    "convergence is derived only after mapping"
                ),
                "detector_tag_mapping": {
                    "physical_left": "trace_tag_1",
                    "physical_right": "trace_tag_0",
                },
                "sample_count": len(samples),
                "phases": {
                    name: sum(sample["phase"] == name for sample in samples)
                    for name in ("gaze", "convergence")
                },
                "left": left,
                "right": right,
                "derived_convergence": convergence,
                "quality_gate": {
                    "status": "pass" if gaze_pass and convergence_pass else (
                        "partial_pass" if gaze_pass or convergence_pass else "fail"
                    ),
                    "gaze_pass": gaze_pass,
                    "convergence_pass": convergence_pass,
                },
            }
            self.output_path.parent.mkdir(parents=True, exist_ok=True)
            sample_path = self.output_path.with_suffix(".samples.jsonl")
            with sample_path.open("w", encoding="utf-8") as output:
                for sample in samples:
                    output.write(json.dumps(sample, separators=(",", ":")) + "\n")
            result["sample_path"] = str(sample_path)
            self.output_path.write_text(json.dumps(result, indent=2), encoding="utf-8")
            with self._lock:
                self.result = result
                self.phase = "done"
                self.message = (
                    f"Saved ({result['quality_gate']['status']}). Gaze MAE "
                    f"L {left['held_out']['angular_mae_deg']:.2f}, "
                    f"R {right['held_out']['angular_mae_deg']:.2f} deg; moving-depth "
                    f"vergence MAE {convergence['convergence']['mae_deg']:.2f} deg. Press Q."
                )
            try:
                self._send_routine("close", 1)
            except OSError:
                pass
        except Exception as error:
            with self._lock:
                self.phase = "error"
                self.message = f"Visual-axis calibration failed: {error}"

    def status(self) -> tuple[str, str, int]:
        with self._lock:
            return self.phase, self.message, len(self.samples)


__all__ = [
    "VisualAxisCalibrationController",
    "apply_axis_eye_mapping",
    "evaluate_independent_convergence",
    "fit_axis_eye_mapping",
]
