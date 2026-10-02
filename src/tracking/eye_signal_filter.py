#!/usr/bin/env python3
"""One Euro filtering, used by the eye gaze runtime and the face event layer."""

from __future__ import annotations

import collections
import math

import numpy as np


def _alpha(cutoff_hz, elapsed_s: float):
    rate = 2.0 * math.pi * cutoff_hz * elapsed_s
    return rate / (rate + 1.0)


class OneEuroVectorFilter:
    """Timestamp-aware One Euro filter; each component gets its own adaptive cutoff."""

    def __init__(
        self,
        dimensions: int,
        min_cutoff_hz: float = 1.5,
        beta: float = 0.08,
        derivative_cutoff_hz: float = 1.0,
    ) -> None:
        if dimensions < 1:
            raise ValueError("dimensions must be positive")
        self.dimensions = dimensions
        self.min_cutoff_hz = float(min_cutoff_hz)
        self.beta = float(beta)
        self.derivative_cutoff_hz = float(derivative_cutoff_hz)
        self._value: np.ndarray | None = None
        self._derivative = np.zeros(dimensions, dtype=np.float64)
        self._time_s: float | None = None

    def update(self, value, timestamp_s: float) -> np.ndarray:
        current = np.asarray(value, dtype=np.float64)
        if current.shape != (self.dimensions,):
            raise ValueError(
                f"expected a {self.dimensions}-component vector, got {current.shape}"
            )
        if self._value is None or self._time_s is None:
            self._value = current.copy()
            self._time_s = float(timestamp_s)
            return current.copy()

        elapsed = float(timestamp_s) - self._time_s
        self._time_s = float(timestamp_s)
        if not math.isfinite(elapsed) or elapsed <= 1e-6:
            return self._value.copy()
        elapsed = min(elapsed, 0.25)

        raw_derivative = (current - self._value) / elapsed
        self._derivative += _alpha(self.derivative_cutoff_hz, elapsed) * (raw_derivative - self._derivative)
        cutoff = self.min_cutoff_hz + self.beta * np.abs(self._derivative)
        self._value += _alpha(cutoff, elapsed) * (current - self._value)
        return self._value.copy()


class IndependentEyeFilter:
    """Three-frame median, then One Euro over both eyes at once. Cutoffs are per component, so neither eye's
    motion changes the other's smoothing."""

    def __init__(self, dimensions: int, **one_euro: float) -> None:
        self.dimensions = dimensions
        self.history: collections.deque[np.ndarray] = collections.deque(maxlen=3)
        self.filter = OneEuroVectorFilter(2 * dimensions, **one_euro)

    def update(self, left, right, timestamp_s: float) -> tuple[np.ndarray, np.ndarray]:
        self.history.append(np.concatenate([left, right]).astype(np.float64))
        both = self.filter.update(np.median(self.history, axis=0), timestamp_s)
        return both[: self.dimensions], both[self.dimensions :]
