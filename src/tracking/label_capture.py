"""Factory (Meta) expression samples forwarded over UDP by the label bridge, for live fusion and calibration."""
from __future__ import annotations

import json
import math
import socket
import threading
import time

FRESH_NS = 100_000_000
EYES = {"leftEyeOrientation": 4, "leftEyePosition": 3, "rightEyeOrientation": 4, "rightEyePosition": 3}


def fresh_values(sample: dict | None, names: list[str], now_ns: int) -> dict[str, float] | None:
    """{name: value} of a native sample at most FRESH_NS from now_ns, else None."""
    if sample is None or not names or abs(int(sample["arrivalMonotonicNs"]) - now_ns) > FRESH_NS:
        return None
    return dict(zip(names, map(float, sample["values"])))


def floats(value, count: int | None = None) -> list[float]:
    if not isinstance(value, list) or len(value) > 512 or count is not None and len(value) != count:
        raise ValueError("invalid list")
    value = [float(v) for v in value]
    if not all(map(math.isfinite, value)):
        raise ValueError("non-finite value")
    return value


def sample_record(m: dict, names: list[str]) -> dict:
    """A bridge sample as recorded: its timing renamed source*, eye fields defaulted, everything checked finite."""
    record = {"type": "sample", "source": str(m.get("source", "Virtual Desktop")), "sourceSequence": int(m["sequence"]),
              "sourceQpc": int(m["qpc"]), "sourceQpcFrequency": int(m["qpcFrequency"]), "sourceUtcUnixMs": int(m["utcUnixMs"]),
              "sourceChangeSequence": int(m.get("sourceChangeSequence", 0)),
              "sourceUnchangedMs": float(m.get("sourceUnchangedMs", -1.)),
              "values": floats(m["values"], len(names) or None), "faceFlags": int(m.get("faceFlags", 0)),
              **{k: bool(m.get(k, False)) for k in ("isEyeFollowingBlendshapesValid", "leftEyeIsValid", "rightEyeIsValid")},
              **{k: float(m.get(k, 0.)) for k in ("leftEyeConfidence", "rightEyeConfidence")},
              **{k: floats(m.get(k, [0.] * n), n) for k, n in EYES.items()}}
    if (record["sourceSequence"] <= 0 or record["sourceQpc"] < 0 or record["sourceQpcFrequency"] <= 0
            or record["sourceChangeSequence"] < 0 or record["sourceUnchangedMs"] < -1
            or not math.isfinite(record["leftEyeConfidence"] + record["rightEyeConfidence"])):
        raise ValueError("invalid sample")
    return record


class LabelSidecarRecorder:
    def __init__(self, port: int = 27274) -> None:
        self.schema_names: list[str] = []
        self.latest: dict | None = None
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._socket.bind(("127.0.0.1", port))
        self._socket.settimeout(0.2)
        self._thread = threading.Thread(target=self._receive, name="vrcft-label-receiver", daemon=True)
        self._thread.start()

    def _receive(self) -> None:
        while self._socket.fileno() != -1:
            try:
                data = self._socket.recv(65507)
            except socket.timeout:
                continue
            except OSError:
                return
            arrival = {"arrivalMonotonicNs": time.monotonic_ns(), "arrivalWallNs": time.time_ns()}
            try:
                message = json.loads(data)
                if message["v"] != 1:
                    continue
                if message["type"] == "schema":
                    names = message["names"]
                    if isinstance(names, list) and 0 < len(names) <= 512 and all(isinstance(n, str) and n for n in names):
                        self.schema_names = names
                elif message["type"] == "sample":
                    self.latest = {**sample_record(message, self.schema_names), **arrival}
            except (KeyError, TypeError, ValueError):
                pass

    def close(self) -> None:
        self._socket.close()
        self._thread.join(timeout=1.0)
