"""Read the headset's per-eye VisualAxisDetector outputs through kernel uprobes, before Meta fuses the two eyes."""

from __future__ import annotations

import base64
import collections
import hashlib
import math
import queue
import re
import struct
import subprocess
import threading
import time
from dataclasses import dataclass, replace

from eye_engine_profile import discover_eye_probe
from headset import ADB, NO_WINDOW, RootShell


ENGINE_PATH = "/odm/lib64/libtrackingengines.so"
TRACE_ROOT = "/sys/kernel/tracing"
TRACE_INSTANCE = "qpro_raw_eye"
TRACE_GROUP = "qpro_raw_eye"
INSTANCE_PATH = f"{TRACE_ROOT}/instances/{TRACE_INSTANCE}"

DETECTOR_SAMPLE = re.compile(
    r"(?P<time>\d+\.\d+): detector_output(?:_secondary)?: .*?"
    r"x=0x(?P<x>[0-9a-fA-F]+) y=0x(?P<y>[0-9a-fA-F]+) "
    r"z=0x(?P<z>[0-9a-fA-F]+) tag=0x(?P<tag>[0-9a-fA-F]+)"
    r"(?: valid=0x(?P<valid>[0-9a-fA-F]+))?"
)


def float_from_trace_hex(value: str) -> float:
    return struct.unpack("<f", struct.pack("<I", int(value, 16)))[0]


def vector_yaw_pitch(vector: tuple[float, float, float]) -> tuple[float, float]:
    x, y, z = vector
    yaw = math.degrees(math.atan2(x, z))
    pitch = math.degrees(math.atan2(-y, math.hypot(x, z)))
    return yaw, pitch


class KernelToPcMonotonicClock:
    """Translate headset kernel timestamps onto the PC monotonic clock.

    ADB trace delivery is bursty, but can only deliver a trace line after it was
    produced.  The lowest recent (arrival - kernel) offset is therefore the
    best available estimate of the two clocks' offset; larger values are USB
    queueing delay, not later capture times.
    """

    def __init__(self, window: int = 1024) -> None:
        self._offsets: collections.deque[int] = collections.deque(maxlen=window)

    def translate(self, kernel_time_s: float, arrival_monotonic_ns: int) -> int:
        kernel_ns = round(float(kernel_time_s) * 1_000_000_000)
        self._offsets.append(int(arrival_monotonic_ns) - kernel_ns)
        return kernel_ns + min(self._offsets)


@dataclass(frozen=True)
class RawEyeSample:
    pc_monotonic_ns: int
    kernel_time_s: float
    left_vector: tuple[float, float, float]
    right_vector: tuple[float, float, float]

    @property
    def left_angles(self) -> tuple[float, float]:
        return vector_yaw_pitch(self.left_vector)

    @property
    def right_angles(self) -> tuple[float, float]:
        return vector_yaw_pitch(self.right_vector)


class DetectorOutputParser:
    """Pair tag-0/tag-1 outputs computed by VisualAxisDetector."""

    def __init__(self) -> None:
        self._pending: dict[int, tuple[float, tuple[float, float, float]]] = {}

    def parse(self, line: str, pc_monotonic_ns: int) -> RawEyeSample | None:
        match = DETECTOR_SAMPLE.search(line)
        if not match:
            return None
        eye = int(match.group("tag"), 16) & 0xFF
        if eye not in (0, 1):
            return None
        if match.group("valid") is not None and int(match.group("valid"), 16) != 1:
            self._pending.pop(eye, None)
            return None
        kernel_time = float(match.group("time"))
        vector = tuple(
            float_from_trace_hex(match.group(axis)) for axis in ("x", "y", "z")
        )
        if not all(math.isfinite(value) for value in vector):
            self._pending.pop(eye, None)
            return None
        if match.group("valid") is not None and not 0.25 <= sum(value * value for value in vector) <= 2.25:
            self._pending.pop(eye, None)
            return None
        self._pending[eye] = (kernel_time, vector)
        if 0 not in self._pending or 1 not in self._pending:
            return None
        left_time, left_vector = self._pending[0]
        right_time, right_vector = self._pending[1]
        if abs(right_time - left_time) > 0.004:
            older = 0 if left_time < right_time else 1
            del self._pending[older]
            return None
        self._pending.clear()
        return RawEyeSample(pc_monotonic_ns, (left_time + right_time) / 2.0, left_vector, right_vector)


ENGINE_PROFILES: dict[int, dict[str, object]] = {
    47_724_232: {
        "event": "detector_output",
        "offset": 0xB63FE4,
        "fetch": ("x=+0x30(%sp):x32 y=+0x34(%sp):x32 z=+0x38(%sp):x32 "
                  "tag=+0x0(%x19):x32"),
    },
    47_418_280: {
        "event": "detector_output",
        "offset": 0xB1F470,
        "extra_offsets": [0xB1F4E4],
        "fetch": ("x=+0x300(%x20):x32 y=+0x304(%x20):x32 z=+0x308(%x20):x32 "
                  "tag=+0x0(%x20):x8 valid=+0x30c(%x20):x8"),
    },
}


class RawTraceEyeReader:
    def __init__(self) -> None:
        self.samples: queue.Queue[RawEyeSample] = queue.Queue(maxsize=512)
        self.errors: queue.Queue[str] = queue.Queue(maxsize=16)
        self._process: subprocess.Popen[str] | None = None
        self._thread: threading.Thread | None = None
        self._stopping = False
        self._clock = KernelToPcMonotonicClock()
        self._shell: RootShell | None = None
        self._profile: dict[str, object] | None = None

    def _cleanup(self) -> None:
        shell = self._shell
        shell.run(f"echo 0 > {INSTANCE_PATH}/tracing_on", check=False)
        for name in ("detector_output", "detector_output_secondary", "eye_visual_axis"):
            shell.run(f"echo 0 > {INSTANCE_PATH}/events/{TRACE_GROUP}/{name}/enable", check=False)
            shell.run(f"echo '-:{TRACE_GROUP}/{name}' >> {TRACE_ROOT}/uprobe_events", check=False)
        shell.run("for qpro_pid in $(pidof cat); do "
                  f"grep -q '{INSTANCE_PATH}/trace_pipe' /proc/$qpro_pid/cmdline && kill $qpro_pid; done",
                  check=False, timeout=2.0)
        for _ in range(8):
            shell.run(f"echo 1 > {INSTANCE_PATH}/free_buffer", check=False, timeout=2.0)
            if shell.run(f"rmdir {INSTANCE_PATH}; test ! -d {INSTANCE_PATH}", check=False, timeout=2.0) is not None:
                return
            time.sleep(0.15)
        raise RuntimeError("Could not release the previous headset eye-trace workspace. "
                           "Stop any other independent-gaze session and try again.")

    def _discover_profile(self, engine_size: int) -> dict[str, object]:
        print("Locating independent-eye probes for this headset build...", flush=True)
        result = subprocess.run([ADB, "exec-out", "cat", ENGINE_PATH], stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, timeout=30, check=False, creationflags=NO_WINDOW)
        if result.returncode != 0 or len(result.stdout) != engine_size:
            raise RuntimeError("Could not read the headset tracking engine for automatic eye-probe detection")
        remote_hash = self._shell.run(f"sha256sum {ENGINE_PATH}").split()
        if not remote_hash or hashlib.sha256(result.stdout).hexdigest() != remote_hash[0]:
            raise RuntimeError("The tracking engine changed or its transfer failed verification. Try again.")
        try:
            profile = discover_eye_probe(result.stdout)
        except ValueError as error:
            raise RuntimeError(
                "Automatic independent-eye detection does not recognize this tracking-engine layout. "
                "Turn off Independent eye gaze to use standard eye tracking. "
                f"Detection detail: {error}"
            ) from error
        print(f"Independent-eye probes detected at 0x{profile['offset']:x} and 0x{profile['extra_offsets'][0]:x}; "
              f"eye-vector offset 0x{profile['axis_offset']:x}.", flush=True)
        return profile

    def start(self) -> None:
        self._shell = RootShell()
        try:
            try:
                engine_size = int(self._shell.run(f"stat -c %s {ENGINE_PATH}").splitlines()[-1])
            except (ValueError, IndexError) as error:
                raise RuntimeError("Could not verify the headset tracking-engine build") from error
            self._profile = ENGINE_PROFILES.get(engine_size) or self._discover_profile(engine_size)
            self._cleanup()
            self._shell.run(f"mkdir {INSTANCE_PATH}")
            for index, offset in enumerate([self._profile["offset"], *self._profile.get("extra_offsets", [])]):
                event = self._profile["event"] + ("_secondary" if index else "")
                probe = f"p:{TRACE_GROUP}/{event} {ENGINE_PATH}:0x{offset:x} {self._profile['fetch']}"
                encoded = base64.b64encode(probe.encode("utf-8")).decode("ascii")
                self._shell.run(f"echo {encoded} | base64 -d >> {TRACE_ROOT}/uprobe_events")
                self._shell.run(f"echo 1 > {INSTANCE_PATH}/events/{TRACE_GROUP}/{event}/enable")
            self._shell.run(f"echo 1 > {INSTANCE_PATH}/tracing_on")
            self._process = subprocess.Popen(
                [ADB, "shell", f"su -c 'cat {INSTANCE_PATH}/trace_pipe'"], stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace", bufsize=1,
                creationflags=NO_WINDOW)
            self._thread = threading.Thread(target=self._read, daemon=True)
            self._thread.start()
        except Exception:
            try:
                self.close()
            except Exception as cleanup:
                print("Eye-trace cleanup also failed: " + str(cleanup), flush=True)
            raise

    def _read(self) -> None:
        parser = DetectorOutputParser()
        try:
            for line in self._process.stdout:
                if self._stopping:
                    break
                arrival_monotonic_ns = time.monotonic_ns()
                sample = parser.parse(line, arrival_monotonic_ns)
                if sample is None:
                    continue
                sample = replace(sample, pc_monotonic_ns=self._clock.translate(sample.kernel_time_s, arrival_monotonic_ns))
                try:
                    self.samples.put_nowait(sample)
                except queue.Full:
                    try:
                        self.samples.get_nowait()
                    except queue.Empty:
                        pass
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
                self._process.wait(timeout=1.0)
            except subprocess.TimeoutExpired:
                self._process.kill()
        if self._thread is not None:
            self._thread.join(timeout=1.0)
        if self._shell is not None:
            try:
                self._cleanup()
            finally:
                self._shell.close()
                self._shell = None
