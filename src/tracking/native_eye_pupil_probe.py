#!/usr/bin/env python3

from __future__ import annotations

import base64
import collections
import queue
import re
import struct
import subprocess
import threading
import time
from dataclasses import dataclass

import numpy as np


ENGINE_PATH = "/odm/lib64/libtrackingengines.so"
EXPECTED_ENGINE_SIZE = 47_724_232
TRACE_ROOT = "/sys/kernel/tracing"
TRACE_INSTANCE = "qpro_neural_pupil"
TRACE_GROUP = "qpro_neural_pupil"

PROBE_OFFSET = 0xB4DE78

LEFT_MEAN = np.asarray([-0.0017055189605983, 0.0045290040805944, -0.01040792463589])
RIGHT_MEAN = np.asarray([0.00046130682644319, 0.0017377919915684, -0.010235720690707])
LEFT_SCALE = np.asarray([0.0035429674058232, 0.0050770516145521, 0.0042806321969521])
RIGHT_SCALE = np.asarray([0.0047817454012475, 0.0055140730704888, 0.0051256563049549])

TRACE_SAMPLE = re.compile(
    r"(?P<time>\d+\.\d+): neural_pupil: .*?"
    r"lx=0x(?P<lx>[0-9a-fA-F]+) ly=0x(?P<ly>[0-9a-fA-F]+) "
    r"lz=0x(?P<lz>[0-9a-fA-F]+) rx=0x(?P<rx>[0-9a-fA-F]+) "
    r"ry=0x(?P<ry>[0-9a-fA-F]+) rz=0x(?P<rz>[0-9a-fA-F]+) "
    r"lax=0x(?P<lax>[0-9a-fA-F]+) lay=0x(?P<lay>[0-9a-fA-F]+) "
    r"laz=0x(?P<laz>[0-9a-fA-F]+) rax=0x(?P<rax>[0-9a-fA-F]+) "
    r"ray=0x(?P<ray>[0-9a-fA-F]+) raz=0x(?P<raz>[0-9a-fA-F]+)"
)


def float_from_trace_hex(value: str) -> float:
    return struct.unpack("<f", struct.pack("<I", int(value, 16)))[0]


@dataclass(frozen=True)
class PupilSample:
    pc_monotonic_ns: int
    arrival_monotonic_ns: int
    kernel_time_s: float
    left_pupil_3d: tuple[float, float, float]
    right_pupil_3d: tuple[float, float, float]
    left_model_axis: tuple[float, float, float]
    right_model_axis: tuple[float, float, float]


class KernelToPcMonotonicClock:
    """Translate headset kernel timestamps onto the PC monotonic clock.

    ADB trace delivery is bursty, but can only deliver a trace line after it was
    produced.  The lowest recent (arrival - kernel) offset is therefore the
    best available estimate of the two clocks' offset; larger values are USB
    queueing delay, not later capture times.
    """

    def __init__(self, window: int = 1024) -> None:
        if window < 1:
            raise ValueError("window must be positive")
        self._offsets: collections.deque[int] = collections.deque(maxlen=window)

    def translate(self, kernel_time_s: float, arrival_monotonic_ns: int) -> int:
        kernel_ns = round(float(kernel_time_s) * 1_000_000_000)
        self._offsets.append(int(arrival_monotonic_ns) - kernel_ns)
        return kernel_ns + min(self._offsets)

    @property
    def estimated_offset_ns(self) -> int | None:
        return min(self._offsets) if self._offsets else None


class NeuralPupilReader:
    def __init__(self, adb: str) -> None:
        self.adb = adb
        self.samples: queue.Queue[PupilSample] = queue.Queue(maxsize=512)
        self.errors: queue.Queue[str] = queue.Queue(maxsize=16)
        self._process: subprocess.Popen[str] | None = None
        self._thread: threading.Thread | None = None
        self._stopping = False
        self._configured = False
        self._clock = KernelToPcMonotonicClock()

    @property
    def instance_path(self) -> str:
        return f"{TRACE_ROOT}/instances/{TRACE_INSTANCE}"

    def _adb_root(self, command: str, *, check: bool = True) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [self.adb, "shell", "su", "-c", command],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=8,
            check=check,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )

    def _cleanup(self) -> None:
        self._adb_root(f"echo 0 '>' {self.instance_path}/tracing_on", check=False)
        self._adb_root(
            f"echo 0 '>' {self.instance_path}/events/{TRACE_GROUP}/neural_pupil/enable",
            check=False,
        )
        self._adb_root(
            f"echo '-:{TRACE_GROUP}/neural_pupil' '>' {TRACE_ROOT}/uprobe_events",
            check=False,
        )
        self._adb_root(f"rmdir {self.instance_path}", check=False)
        self._configured = False

    def start(self) -> None:
        state = subprocess.run(
            [self.adb, "get-state"],
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            timeout=5,
            check=False,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        if state.returncode != 0 or state.stdout.strip() != "device":
            raise RuntimeError("No authorized Quest was found over ADB")
        size_result = self._adb_root(f"stat -c %s {ENGINE_PATH}")
        try:
            engine_size = int(size_result.stdout.strip().splitlines()[-1])
        except (ValueError, IndexError) as error:
            raise RuntimeError("Could not verify the headset tracking-engine build") from error
        if engine_size != EXPECTED_ENGINE_SIZE:
            raise RuntimeError(
                f"Tracking-engine size changed ({engine_size}, expected "
                f"{EXPECTED_ENGINE_SIZE}); the firmware-specific offset is unsafe"
            )

        self._cleanup()
        self._adb_root(f"mkdir {self.instance_path}")
        event = (
            f"p:{TRACE_GROUP}/neural_pupil {ENGINE_PATH}:0x{PROBE_OFFSET:x} "
            "lx=+0xd4(%x19):x32 ly=+0xd8(%x19):x32 lz=+0xdc(%x19):x32 "
            "rx=+0xe0(%x19):x32 ry=+0xe4(%x19):x32 rz=+0xe8(%x19):x32 "
            "lax=+0x58(%x19):x32 lay=+0x5c(%x19):x32 laz=+0x60(%x19):x32 "
            "rax=+0x64(%x19):x32 ray=+0x68(%x19):x32 raz=+0x6c(%x19):x32"
        )
        encoded = base64.b64encode(event.encode()).decode()
        try:
            self._adb_root(
                f"echo {encoded} '|' base64 -d '>>' {TRACE_ROOT}/uprobe_events"
            )
            self._adb_root(
                f"echo 1 '>' {self.instance_path}/events/{TRACE_GROUP}/neural_pupil/enable"
            )
            self._adb_root(f"echo 1 '>' {self.instance_path}/tracing_on")
            self._configured = True
            self._process = subprocess.Popen(
                [self.adb, "shell", f"su -c 'cat {self.instance_path}/trace_pipe'"],
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            self._thread = threading.Thread(target=self._read, daemon=True)
            self._thread.start()
        except Exception:
            self._cleanup()
            raise

    def _read(self) -> None:
        assert self._process is not None and self._process.stdout is not None
        try:
            for line in self._process.stdout:
                if self._stopping:
                    break
                match = TRACE_SAMPLE.search(line)
                if match is None:
                    continue
                arrival_monotonic_ns = time.monotonic_ns()
                kernel_time_s = float(match.group("time"))
                sample = PupilSample(
                    pc_monotonic_ns=self._clock.translate(
                        kernel_time_s, arrival_monotonic_ns
                    ),
                    arrival_monotonic_ns=arrival_monotonic_ns,
                    kernel_time_s=kernel_time_s,
                    left_pupil_3d=tuple(
                        float_from_trace_hex(match.group("l" + axis))
                        for axis in ("x", "y", "z")
                    ),
                    right_pupil_3d=tuple(
                        float_from_trace_hex(match.group("r" + axis))
                        for axis in ("x", "y", "z")
                    ),
                    left_model_axis=tuple(
                        float_from_trace_hex(match.group("la" + axis))
                        for axis in ("x", "y", "z")
                    ),
                    right_model_axis=tuple(
                        float_from_trace_hex(match.group("ra" + axis))
                        for axis in ("x", "y", "z")
                    ),
                )
                try:
                    self.samples.put_nowait(sample)
                except queue.Full:
                    self.samples.get_nowait()
                    self.samples.put_nowait(sample)
        except Exception as error:
            try:
                self.errors.put_nowait(str(error))
            except queue.Full:
                pass

    def close(self) -> None:
        self._stopping = True
        if self._process is not None and self._process.poll() is None:
            self._process.terminate()
            try:
                self._process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                self._process.kill()
        if self._thread is not None:
            self._thread.join(timeout=1)
        if self._configured:
            self._cleanup()
