"""VRCFT tongue output (UDP 27275, the module's one port): packet format and fail-safe broadcaster."""

from __future__ import annotations

import socket
import struct
import time

import numpy as np

TONGUE_PACKET = struct.Struct("<4sBBH12f")
TONGUE_MAGIC = b"QPTO"
TONGUE_VERSION = 3
TONGUE_MASK = 0b11111


def encode_tongue_packet(values: np.ndarray, enabled: bool) -> bytes:
    values = np.asarray(values, dtype=np.float32)
    if values.shape != (12,) or not np.isfinite(values).all():
        raise ValueError("A VRCFT tongue packet needs twelve finite values")
    return TONGUE_PACKET.pack(TONGUE_MAGIC, TONGUE_VERSION, int(enabled), TONGUE_MASK, *values)


class TongueBroadcaster:
    """Opt-in UDP override; disabled or stale packets restore stock tracking. Used from the receiver's main thread only."""

    def __init__(self, port: int = 27275, *, enabled: bool = False) -> None:
        self.enabled = bool(enabled)
        self._address = ("127.0.0.1", int(port))
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._last_values: np.ndarray | None = None
        self._last_sent = 0.0

    def send(self, extension: float, horizontal: float, vertical: float, visible: bool, *, now: float | None = None) -> None:
        """Model heads to VRCFT's slots (signed heads split in two, all 0 while hidden). Every frame that moved a value by
        .015 goes out; an unchanged one at most every 200 ms, as the keepalive."""
        if not self.enabled:
            return
        values = np.zeros(12, dtype=np.float32)
        if visible:
            values[:5] = extension, vertical, -vertical, -horizontal, horizontal
        np.clip(values, 0, 1, out=values)
        now = time.perf_counter() if now is None else now
        if (self._last_values is not None and float(np.max(np.abs(values - self._last_values))) < 0.015
                and now - self._last_sent < 0.20):
            return
        self._socket.sendto(encode_tongue_packet(values, True), self._address)
        self._last_values, self._last_sent = values, now

    def close(self) -> None:
        try:
            self.enabled = False
            self._socket.sendto(encode_tongue_packet(np.zeros(12), False), self._address)
        finally:
            self._socket.close()
