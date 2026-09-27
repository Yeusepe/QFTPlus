
"""Live preview and fail-safe VRCFT output for the stereo tongue model."""

from __future__ import annotations

import time
import socket
import struct
import threading
from dataclasses import dataclass, replace
from pathlib import Path

import cv2
import numpy as np
import torch

from train_tongue_model import create_model
from inference_backend import prepare_inputs


TONGUE_PACKET = struct.Struct("<4sBBH12f")
TONGUE_MAGIC = b"QPTO"
TONGUE_VERSION = 1
TONGUE_PACKET_V2 = struct.Struct("<4sBBH12f2d")
OUTPUT_HEADS = ("extension", "vertical", "vertical", "horizontal", "horizontal",
                "roll", "bend_down", "curl_up", "squish", "flat", "twist", "twist")


def vrcft_tongue_values(
    prediction: "TonguePrediction", target_names: list[str]
) -> np.ndarray:
    """Map ten model heads to VRCFT's twelve detailed tongue expressions."""
    values = {
        name: float(prediction.values[index])
        for index, name in enumerate(target_names)
    }
    if not prediction.visible:
        return np.zeros(12, dtype=np.float32)
    horizontal = float(np.clip(values.get("horizontal", 0.0), -1.0, 1.0))
    vertical = float(np.clip(values.get("vertical", 0.0), -1.0, 1.0))
    twist = float(np.clip(values.get("twist", 0.0), -1.0, 1.0))
    tongue_out = float(np.clip(values.get("extension", 0.0), 0.0, 1.0))
    return np.asarray(
        [
            tongue_out,
            max(vertical, 0.0),
            max(-vertical, 0.0),
            max(-horizontal, 0.0),
            max(horizontal, 0.0),
            float(np.clip(values.get("roll", 0.0), 0.0, 1.0)),
            float(np.clip(values.get("bend_down", 0.0), 0.0, 1.0)),
            float(np.clip(values.get("curl_up", 0.0), 0.0, 1.0)),
            float(np.clip(values.get("squish", 0.0), 0.0, 1.0)),
            float(np.clip(values.get("flat", 0.0), 0.0, 1.0)),
            max(-twist, 0.0),
            max(twist, 0.0),
        ],
        dtype=np.float32,
    )


def encode_tongue_packet(values: np.ndarray, enabled: bool, *, supported: list[str] | None = None,
                         received_at: float = 0.0, sent_at: float = 0.0) -> bytes:
    values = np.asarray(values, dtype=np.float32)
    if values.shape != (12,):
        raise ValueError("A VRCFT tongue packet needs exactly twelve values")
    if not np.isfinite(values).all():
        raise ValueError("Tongue output must be finite")
    if supported is not None:
        mask = sum(1 << i for i, name in enumerate(OUTPUT_HEADS) if name in supported)
        return TONGUE_PACKET_V2.pack(TONGUE_MAGIC, 2, int(enabled), mask,
            *np.clip(values, 0, 1), received_at, sent_at)
    return TONGUE_PACKET.pack(
        TONGUE_MAGIC, TONGUE_VERSION, int(enabled), 0,
        *[float(np.clip(value, 0.0, 1.0)) for value in values],
    )


class TongueBroadcaster:
    """Opt-in UDP override; disabled or stale packets restore stock tracking."""

    def __init__(self, port: int = 27276, *, enabled: bool = False,
                 supported: list[str] | None = None) -> None:
        self.enabled = bool(enabled)
        self.supported = supported
        self.received_at = 0.0
        self._address = ("127.0.0.1", int(port))
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._last_values: np.ndarray | None = None
        self._last_sent = 0.0
        self._minimum_interval = 1.0 / 24.0
        self._keepalive_interval = 0.20
        self._lock = threading.RLock()

    def toggle(self) -> bool:
        self.set_enabled(not self.enabled)
        return self.enabled

    def set_enabled(self, enabled: bool) -> None:
        with self._lock:
            if self.enabled == bool(enabled):
                return
            self.enabled = bool(enabled)
            self._last_values = None
            self._last_sent = 0.0
            if not self.enabled:
                self._send(np.zeros(12, dtype=np.float32), enabled=False)

    def send_prediction(
        self, prediction: "TonguePrediction", target_names: list[str], *, now: float | None = None
    ) -> None:
        with self._lock:
            self._send_prediction(prediction, target_names, now=now)

    def _send_prediction(self, prediction, target_names, *, now=None):
        if not self.enabled:
            return
        values = vrcft_tongue_values(prediction, target_names)
        self.received_at = prediction.received_at
        now = time.perf_counter() if now is None else now
        elapsed = now - self._last_sent
        if elapsed < self._minimum_interval:
            return
        changed = (
            self._last_values is None
            or float(np.max(np.abs(values - self._last_values))) >= 0.015
        )
        if not changed and elapsed < self._keepalive_interval:
            return
        self._send(values, enabled=True)
        self._last_values = values.copy()
        self._last_sent = now

    def _send(self, values: np.ndarray, *, enabled: bool) -> None:
        self._socket.sendto(encode_tongue_packet(values, enabled, supported=self.supported,
            received_at=self.received_at, sent_at=time.perf_counter()), self._address)

    def close(self) -> None:
        with self._lock:
            try:
                self.enabled = False
                self._send(np.zeros(12, dtype=np.float32), enabled=False)
            finally:
                self._socket.close()


@dataclass(frozen=True)
class TonguePrediction:
    values: np.ndarray
    native_tongue_out: float
    fused_visibility: float
    visible: bool
    inference_ms: float
    pipeline_ms: float = 0.0
    dropped_frames: int = 0
    preprocessing_ms: float = 0.0
    receiver_ms: float = 0.0
    queue_ms: float = 0.0
    received_at: float = 0.0


def supported_targets(checkpoint: dict) -> list[str]:
    names = list(checkpoint["targetNames"])

    supported = checkpoint.get("supportedTargets", ["visibility", "extension", "horizontal", "vertical"])
    if not isinstance(supported, list) or not set(supported) <= set(names):
        raise ValueError("Invalid checkpoint supported-target schema")
    return supported


class LiveTongueModelPreview:
    def __init__(
        self,
        checkpoint_path: str | Path,
        device_name: str = "auto",
        direction_checkpoint_path: str | Path | None = None,
        smoothing: float = 0.35,
        visibility_mode: str = "weighted",
        camera_weight: float | None = None,
    ) -> None:
        self.checkpoint_path = Path(checkpoint_path).resolve()
        from inference_backend import select_device, prepare_model
        selected = select_device(device_name)
        self.device = torch.device("cpu" if selected == "directml" else selected)
        checkpoint = torch.load(self.checkpoint_path, map_location="cpu", weights_only=True)
        self.target_names = list(checkpoint["targetNames"])
        self.supported_targets = supported_targets(checkpoint)
        self.image_size = int(checkpoint["imageSize"])
        self.architecture = str(checkpoint.get("architecture", "legacy-late-fusion-v1"))
        self.model = create_model(self.architecture, self.target_names)
        self.model.load_state_dict(checkpoint["modelState"])
        self.model.to(self.device).eval()
        self.direction_checkpoint_path: Path | None = None
        self.direction_model = None
        self.direction_image_size = self.image_size
        if direction_checkpoint_path:
            self.direction_checkpoint_path = Path(direction_checkpoint_path).resolve()
            direction_checkpoint = torch.load(
                self.direction_checkpoint_path, map_location="cpu", weights_only=True
            )
            direction_names = list(direction_checkpoint["targetNames"])
            if direction_names != self.target_names:
                raise ValueError(
                    "Visibility and direction checkpoints use different target schemas"
                )
            direction_architecture = str(
                direction_checkpoint.get("architecture", "legacy-late-fusion-v1")
            )
            self.direction_image_size = int(direction_checkpoint["imageSize"])
            self.direction_model = create_model(
                direction_architecture, self.target_names
            )
            self.direction_model.load_state_dict(direction_checkpoint["modelState"])
            self.direction_model.to(self.device).eval()
            self.supported_targets = [n for n in supported_targets(direction_checkpoint) if n != "visibility"]
            if "visibility" in supported_targets(checkpoint):
                self.supported_targets.append("visibility")
        if "visibility" not in self.supported_targets:
            raise ValueError("Tongue inference requires a supported visibility gate")
        self.paired = ((selected.startswith("cuda") or selected == "directml") and self.direction_model is not None
                       and self.direction_image_size == self.image_size
                       and self.image_size in (64, 96, 128, 160, 192, 224, 256))
        if self.paired:
            from inference_backend import prepare_tongue_pair
            self.model = prepare_tongue_pair(self.model, self.direction_model, self.image_size,
                                            selected=selected, checkpoint=self.checkpoint_path)
            self.direction_model = None
        else:
            self.model = prepare_model(self.model, self.checkpoint_path, (1, 2, self.image_size, self.image_size), selected)
            if self.direction_model is not None:
                self.direction_model = prepare_model(self.direction_model, self.direction_checkpoint_path,
                    (1, 2, self.direction_image_size, self.direction_image_size), selected)
        gate = checkpoint.get("visibilityGate", {})
        self.camera_weight = float(
            gate.get("cameraWeight", 0.5) if camera_weight is None else camera_weight
        )
        self.threshold = float(gate.get("threshold", 0.44))
        self.smoothing = float(np.clip(smoothing, 0.05, 1.0))
        if visibility_mode not in {"camera", "native", "weighted", "agreement"}:
            raise ValueError(f"Unsupported tongue visibility mode: {visibility_mode}")
        self.visibility_mode = visibility_mode
        self._smoothed: np.ndarray | None = None
        self._visible_latched = False

    def _inputs(self, strip: np.ndarray, image_size: int, model=None) -> torch.Tensor:
        return prepare_inputs(self.model if model is None else model, strip, image_size, 2)

    def predict(
        self,
        strip: np.ndarray,
        factory_sample: dict[str, object] | None,
        factory_names: list[str],
    ) -> TonguePrediction:
        if strip.shape not in ((400, 800), (400, 1200)):
            raise ValueError(
                "Tongue preview requires cameras 2 and 3 in a 400x800 mouth "
                f"or 400x1200 face strip, got {strip.shape}"
            )
        preprocessing_started = time.perf_counter()
        inputs = self._inputs(strip, self.image_size)
        raw = getattr(self.model, "raw_input", False)
        same_input = (getattr(self.direction_model, "raw_input", False) == raw
                      and (raw or self.direction_image_size == self.image_size))
        direction_inputs = (inputs if same_input else
            self._inputs(strip, self.direction_image_size, self.direction_model)) if self.direction_model is not None else None
        started = time.perf_counter()
        preprocessing_ms = (started - preprocessing_started) * 1000
        with torch.inference_mode():
            values = self.model(inputs)
            if self.paired or self.direction_model is not None:


                pair = (values if self.paired else torch.cat((values, self.direction_model(direction_inputs)), dim=0)).float().cpu().numpy()
                visibility_index = self.target_names.index("visibility")
                values = pair[1].copy()
                values[visibility_index] = pair[0, visibility_index]
            else:
                values = values[0].float().cpu().numpy()
        inference_ms = (time.perf_counter() - started) * 1000.0
        values[[i for i, n in enumerate(self.target_names) if n not in self.supported_targets]] = 0
        self._smoothed = (
            values
            if self._smoothed is None
            else self.smoothing * values + (1.0 - self.smoothing) * self._smoothed
        )
        native = 0.0
        if factory_sample is not None and "TongueOut" in factory_names:
            native = float(factory_sample["values"][factory_names.index("TongueOut")])
        visibility = float(self._smoothed[self.target_names.index("visibility")])
        if self.visibility_mode == "camera":
            fused = visibility
        elif self.visibility_mode == "native":
            fused = native
        elif self.visibility_mode == "agreement":
            fused = min(visibility, native)
        else:
            fused = self.camera_weight * visibility + (1.0 - self.camera_weight) * native
        self._visible_latched = (
            fused >= self.threshold - 0.08
            if self._visible_latched
            else fused >= self.threshold
        )
        return TonguePrediction(
            values=self._smoothed.copy(),
            native_tongue_out=native,
            fused_visibility=fused,
            visible=self._visible_latched,
            inference_ms=inference_ms,
            preprocessing_ms=preprocessing_ms,
        )

    def render(
        self, prediction: TonguePrediction, *, output_enabled: bool = False
    ) -> np.ndarray:
        image = np.zeros((760, 1100, 3), dtype=np.uint8)
        cv2.putText(image, "Quest Pro personalized stereo tongue preview", (24, 42),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.86, (240, 240, 240), 2, cv2.LINE_AA)
        output_text = (
            "EXPERIMENTAL VRCFT TONGUE OUTPUT ON - T disables immediately"
            if output_enabled
            else "SAFE MODE - stock streaming-app tongue active; T enables experiment"
        )
        cv2.putText(
            image, output_text, (24, 76), cv2.FONT_HERSHEY_SIMPLEX, 0.55,
            (80, 235, 120) if output_enabled else (50, 175, 255), 1, cv2.LINE_AA,
        )
        status = "TONGUE VISIBLE" if prediction.visible else "TONGUE RETRACTED"
        color = (80, 235, 120) if prediction.visible else (170, 170, 170)
        cv2.putText(image, status, (24, 118), cv2.FONT_HERSHEY_SIMPLEX, 0.68,
                    color, 2, cv2.LINE_AA)
        visibility = float(prediction.values[self.target_names.index("visibility")])
        summary = (
            f"camera {visibility:.2f}  native {prediction.native_tongue_out:.2f}  "
            f"{self.visibility_mode} {prediction.fused_visibility:.2f} / threshold {self.threshold:.2f}  "
            f"infer {prediction.inference_ms:.1f} ms  "
            f"pipeline {prediction.pipeline_ms:.1f} ms  dropped {prediction.dropped_frames}"
        )
        cv2.putText(image, summary, (24, 151), cv2.FONT_HERSHEY_SIMPLEX, 0.49,
                    (190, 220, 255), 1, cv2.LINE_AA)
        if self.direction_checkpoint_path is not None:
            cv2.putText(
                image,
                "ENSEMBLE: clean manual visibility gate + dense motion directions",
                (24, 177), cv2.FONT_HERSHEY_SIMPLEX, 0.46,
                (120, 225, 255), 1, cv2.LINE_AA,
            )
        bar_x, bar_width = 260, 560
        for row, (name, value) in enumerate(zip(self.target_names, prediction.values)):
            y = 205 + row * 46
            cv2.putText(image, name, (24, y + 9), cv2.FONT_HERSHEY_SIMPLEX, 0.52,
                        (225, 225, 225), 1, cv2.LINE_AA)
            cv2.rectangle(image, (bar_x, y - 9), (bar_x + bar_width, y + 15),
                          (55, 55, 55), 1)
            if name in {"horizontal", "vertical", "twist"}:
                center = bar_x + bar_width // 2
                cv2.line(image, (center, y - 8), (center, y + 14), (90, 90, 90), 1)
                endpoint = center + round((bar_width / 2) * float(value))
                cv2.rectangle(image, (min(center, endpoint), y - 5),
                              (max(center, endpoint), y + 11), (245, 90, 225), -1)
            else:
                endpoint = bar_x + round(bar_width * float(np.clip(value, 0, 1)))
                cv2.rectangle(image, (bar_x, y - 5), (endpoint, y + 11),
                              (80, 235, 120), -1)
            cv2.putText(image, f"{float(value):+.2f}", (845, y + 9),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.49, (220, 220, 220), 1, cv2.LINE_AA)
        cv2.putText(
            image,
            "T toggles VRCFT output | Q quits and restores stock tongue automatically.",
            (24, 730), cv2.FONT_HERSHEY_SIMPLEX, 0.48, (170, 170, 170), 1, cv2.LINE_AA,
        )
        return image


class TongueInferenceWorker:
    """Runs inference latest-frame-first so brief stalls cannot build latency."""

    def __init__(
        self,
        preview: LiveTongueModelPreview,
        broadcaster: TongueBroadcaster,
        *, render: bool = True,
    ) -> None:
        self.preview = preview
        self.broadcaster = broadcaster
        self.render = render
        self._condition = threading.Condition()
        self._pending: tuple[
            np.ndarray, dict[str, object] | None, list[str], float, float
        ] | None = None
        self._latest: tuple[TonguePrediction, np.ndarray | None] | None = None
        self._error: BaseException | None = None
        self._running = True
        self._dropped_frames = 0
        self._last_timing_log = 0.0
        self._thread = threading.Thread(
            target=self._run, name="tongue-inference", daemon=True
        )
        self._thread.start()

    @property
    def active(self) -> bool:
        return self.render or self.broadcaster.enabled

    def submit(
        self,
        strip: np.ndarray,
        factory_sample: dict[str, object] | None,
        factory_names: list[str],
        received_at: float | None = None,
    ) -> None:
        with self._condition:
            if not self.active or not self._running:
                self._pending = None
                return
            if self._pending is not None:
                self._dropped_frames += 1


            now = time.perf_counter()
            self._pending = (strip, factory_sample, list(factory_names), now,
                             now if received_at is None else received_at)
            self._condition.notify()

    def latest(self) -> tuple[TonguePrediction | None, np.ndarray | None]:
        with self._condition:
            if self._error is not None:
                raise RuntimeError("Tongue inference worker failed") from self._error
            if self._latest is None:
                return None, None
            return self._latest

    def close(self) -> None:
        with self._condition:
            self._running = False
            self._condition.notify_all()



        if self._thread.ident is not None:
            self._thread.join(timeout=2.0)

    def _run(self) -> None:
        try:
            while True:
                with self._condition:
                    while self._running and self._pending is None:
                        self._condition.wait()
                    if not self._running:
                        return
                    strip, factory_sample, factory_names, submitted_at, received_at = self._pending
                    self._pending = None
                    dropped = self._dropped_frames
                if not self.active:
                    continue
                started = time.perf_counter()
                prediction = self.preview.predict(
                    strip, factory_sample, factory_names
                )
                prediction = replace(
                    prediction,
                    pipeline_ms=(time.perf_counter() - received_at) * 1000.0,
                    received_at=received_at,
                    receiver_ms=(submitted_at - received_at) * 1000.0,
                    queue_ms=(started - submitted_at) * 1000.0,
                    dropped_frames=dropped,
                )
                self.broadcaster.send_prediction(
                    prediction, self.preview.target_names
                )
                if time.perf_counter() - self._last_timing_log >= 5:
                    self._last_timing_log = time.perf_counter()
                    print(f"TONGUE_TIMING receiver_ms={prediction.receiver_ms:.2f} "
                          f"queue_ms={prediction.queue_ms:.2f} preprocess_ms={prediction.preprocessing_ms:.2f} "
                          f"inference_ms={prediction.inference_ms:.2f} arrival_to_prediction_ms={prediction.pipeline_ms:.2f} "
                          f"arrival_to_publication_check_ms={(self._last_timing_log-received_at)*1000:.2f} "
                          f"dropped={dropped} publication_cap_hz=24", flush=True)
                image = self.preview.render(
                    prediction, output_enabled=self.broadcaster.enabled
                ) if self.render else None
                with self._condition:
                    self._latest = (prediction, image)
        except BaseException as error:
            with self._condition:
                self._error = error
                self._running = False
                self._condition.notify_all()
