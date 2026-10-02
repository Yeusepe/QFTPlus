"""VRCFT tongue output (UDP 27276): packet format, fail-safe broadcaster and the prediction record.
No PyTorch: the universal face model sends tongue direction through this too."""

from __future__ import annotations

import socket
import struct
import threading
import time
from dataclasses import dataclass

import numpy as np

TONGUE_PACKET = struct.Struct("<4sBBH12f")
TONGUE_MAGIC = b"QPTO"
TONGUE_VERSION = 1
TONGUE_PACKET_V2 = struct.Struct("<4sBBH12f2d")
NATIVE_MAX_AGE_NS = 250_000_000
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
        *np.clip(values, 0, 1),
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
