#!/usr/bin/env python3
"""One Euro filtering of the gap between the eyes for independent gaze (convergence)."""

from __future__ import annotations

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
    """Smooths only half the gap between the eyes, which convergence strength multiplies: it is mostly the eyes'
    independent noise, while real convergence takes a few hundred ms (1 Hz halves its jitter for ~0.1 s convergence
    lag). The shared direction passes raw; the Gaze Smoothing setting smooths it (0 = raw). Same as Convergence.java."""

    def __init__(self, dimensions: int) -> None:
        self.dimensions = dimensions
        self.gap = OneEuroVectorFilter(dimensions, min_cutoff_hz=1.0, beta=0.03, derivative_cutoff_hz=0.5)

    def update(self, left, right, timestamp_s: float) -> tuple[np.ndarray, np.ndarray]:
        left, right = np.asarray(left, dtype=np.float64), np.asarray(right, dtype=np.float64)
        shared = (left + right) / 2
        gap = self.gap.update((left - right) / 2, timestamp_s)
        return shared + gap, shared - gap
