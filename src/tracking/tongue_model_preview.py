
"""Live preview and fail-safe VRCFT output for the stereo tongue model."""

from __future__ import annotations

import time
import threading
from dataclasses import replace
from pathlib import Path

import cv2
import numpy as np
import torch

from train_tongue_model import create_model
from inference_backend import prepare_inputs
from face_events import TONGUE, FaceEvents


from tongue_output import (
    NATIVE_MAX_AGE_NS, OUTPUT_HEADS, TONGUE_MAGIC, TONGUE_PACKET, TONGUE_PACKET_V2, TONGUE_VERSION,
    TongueBroadcaster, TonguePrediction, encode_tongue_packet, vrcft_tongue_values,
)


def supported_targets(checkpoint: dict) -> list[str]:
    names = list(checkpoint["targetNames"])

    supported = checkpoint.get("supportedTargets", ["visibility", "extension", "horizontal", "vertical"])
    if not isinstance(supported, list) or not set(supported) <= set(names):
        raise ValueError("Invalid checkpoint supported-target schema")
    return supported


class LiveTongueModelPreview:
    """Our model gives direction and extension; Meta's native TongueOut decides whether the tongue is out."""

    def __init__(
        self,
        checkpoint_path: str | Path,
        device_name: str = "auto",
        smoothing: float = 0.35,
        bias: float = 0.,
        log: str | None = None,
    ) -> None:
        self.checkpoint_path = Path(checkpoint_path).resolve()
        from inference_backend import select_device, prepare_model
        selected = select_device(device_name)
        self.device = torch.device("cpu" if selected == "directml" else selected)
        checkpoint = torch.load(self.checkpoint_path, map_location="cpu", weights_only=True)
        self.target_names = list(checkpoint["targetNames"])
        self.supported_targets = [n for n in supported_targets(checkpoint) if n != "visibility"]
        self.image_size = int(checkpoint["imageSize"])
        self.architecture = str(checkpoint.get("architecture", "legacy-late-fusion-v1"))
        model = create_model(self.architecture, self.target_names)
        model.load_state_dict(checkpoint["modelState"])
        model.to(self.device).eval()
        self.model = prepare_model(model, self.checkpoint_path, (1, 2, self.image_size, self.image_size), selected)
        self.smoothing = float(np.clip(smoothing, 0.05, 1.0))
        self._smoothed: np.ndarray | None = None
        self.events = FaceEvents({"TongueOut": TONGUE}, bias=bias, log=log)

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
        started = time.perf_counter()
        preprocessing_ms = (started - preprocessing_started) * 1000
        with torch.inference_mode():
            values = self.model(inputs)[0].float().cpu().numpy()
        inference_ms = (time.perf_counter() - started) * 1000.0
        values[[i for i, n in enumerate(self.target_names) if n not in self.supported_targets]] = 0
        self._smoothed = (
            values
            if self._smoothed is None
            else self.smoothing * values + (1.0 - self.smoothing) * self._smoothed
        )
        native = 0.0
        if (factory_sample is not None and "TongueOut" in factory_names
                and time.monotonic_ns() - int(factory_sample["arrivalMonotonicNs"]) <= NATIVE_MAX_AGE_NS):
            native = float(factory_sample["values"][factory_names.index("TongueOut")])
        fused = native
        self.events.step({"TongueOut": fused}, factory_sample, factory_names, time.monotonic_ns())
        return TonguePrediction(
            values=self._smoothed.copy(),
            native_tongue_out=native,
            fused_visibility=fused,
            visible=self.events.on["TongueOut"],
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
        summary = (
            f"native TongueOut {prediction.native_tongue_out:.2f}  "
            f"infer {prediction.inference_ms:.1f} ms  "
            f"pipeline {prediction.pipeline_ms:.1f} ms  dropped {prediction.dropped_frames}"
        )
        cv2.putText(image, summary, (24, 151), cv2.FONT_HERSHEY_SIMPLEX, 0.49,
                    (190, 220, 255), 1, cv2.LINE_AA)
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
