"""Factory (Meta) expression samples forwarded over UDP by the label bridge, for live fusion and calibration."""

from __future__ import annotations

import collections
import json
import math
import socket
import threading
import time


class LabelSidecarRecorder:
    def __init__(self, port: int = 27274) -> None:
        self.sample_count = 0
        self.invalid_count = 0
        self.schema_names: list[str] = []
        self._recent_lock = threading.Lock()
        self._recent_samples: collections.deque[dict[str, object]] = (
            collections.deque(maxlen=512)
        )
        self._socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._socket.bind(("127.0.0.1", port))
        self._socket.settimeout(0.2)
        self._running = True
        self._thread = threading.Thread(
            target=self._receive_loop,
            name="vrcft-label-receiver",
            daemon=True,
        )
        self._thread.start()

    def _receive_loop(self) -> None:
        while self._running:
            try:
                data, address = self._socket.recvfrom(65507)
            except socket.timeout:
                continue
            except OSError:
                break
            if address[0] != "127.0.0.1":
                self.invalid_count += 1
                continue
            arrival_monotonic_ns = time.monotonic_ns()
            arrival_wall_ns = time.time_ns()
            try:
                message = json.loads(data)
                if not isinstance(message, dict) or message.get("v") != 1:
                    raise ValueError("unsupported message")
                message_type = message.get("type")
                if message_type == "schema":
                    names = message.get("names")
                    if (not isinstance(names, list) or not names
                            or len(names) > 512
                            or any(not isinstance(name, str) or not name
                                   for name in names)):
                        raise ValueError("invalid schema")
                    self.schema_names = names
                elif message_type == "sample":
                    values = message.get("values")
                    if (not isinstance(values, list)
                            or len(values) > 512
                            or (self.schema_names
                                and len(values) != len(self.schema_names))):
                        raise ValueError("invalid values")
                    numeric_values = [float(value) for value in values]
                    if any(not math.isfinite(value) for value in numeric_values):
                        raise ValueError("non-finite value")
                    sequence = int(message["sequence"])
                    qpc = int(message["qpc"])
                    qpc_frequency = int(message["qpcFrequency"])
                    if sequence <= 0 or qpc < 0 or qpc_frequency <= 0:
                        raise ValueError("invalid timing")
                    source_change_sequence = int(
                        message.get("sourceChangeSequence", 0)
                    )
                    source_unchanged_ms = float(
                        message.get("sourceUnchangedMs", -1.0)
                    )
                    if source_change_sequence < 0 or source_unchanged_ms < -1.0:
                        raise ValueError("invalid source freshness")
                    sample_record: dict[str, object] = {
                        "type": "sample",
                        "source": str(message.get("source", "Virtual Desktop")),
                        "arrivalMonotonicNs": arrival_monotonic_ns,
                        "arrivalWallNs": arrival_wall_ns,
                        "sourceSequence": sequence,
                        "sourceQpc": qpc,
                        "sourceQpcFrequency": qpc_frequency,
                        "sourceUtcUnixMs": int(message["utcUnixMs"]),
                        "sourceChangeSequence": source_change_sequence,
                        "sourceUnchangedMs": source_unchanged_ms,
                        "values": numeric_values,
                        "faceFlags": int(message.get("faceFlags", 0)),
                        "isEyeFollowingBlendshapesValid": bool(
                            message.get("isEyeFollowingBlendshapesValid", False)
                        ),
                        "leftEyeIsValid": bool(message.get("leftEyeIsValid", False)),
                        "rightEyeIsValid": bool(message.get("rightEyeIsValid", False)),
                        "leftEyeConfidence": float(
                            message.get("leftEyeConfidence", 0.0)
                        ),
                        "rightEyeConfidence": float(
                            message.get("rightEyeConfidence", 0.0)
                        ),
                    }
                    for field_name, expected_count in (
                        ("leftEyeOrientation", 4),
                        ("leftEyePosition", 3),
                        ("rightEyeOrientation", 4),
                        ("rightEyePosition", 3),
                    ):
                        source_values = message.get(field_name, [0.0] * expected_count)
                        if (not isinstance(source_values, list)
                                or len(source_values) != expected_count):
                            raise ValueError(f"invalid {field_name}")
                        converted = [float(value) for value in source_values]
                        if any(not math.isfinite(value) for value in converted):
                            raise ValueError(f"non-finite {field_name}")
                        sample_record[field_name] = converted
                    numeric_metadata = (
                        float(sample_record["leftEyeConfidence"]),
                        float(sample_record["rightEyeConfidence"]),
                    )
                    if any(not math.isfinite(value) for value in numeric_metadata):
                        raise ValueError("non-finite eye confidence")
                    self.sample_count += 1
                    with self._recent_lock:
                        self._recent_samples.append(sample_record)
                else:
                    raise ValueError("unknown message type")
            except (KeyError, TypeError, ValueError, json.JSONDecodeError):
                self.invalid_count += 1

    def nearest_sample(self, monotonic_ns: int) -> dict[str, object] | None:
        with self._recent_lock:
            if not self._recent_samples:
                return None
            sample = min(
                self._recent_samples,
                key=lambda value: abs(
                    int(value["arrivalMonotonicNs"]) - monotonic_ns
                ),
            )
            return dict(sample)

    def close(self) -> None:
        if not self._running:
            return
        self._running = False
        self._socket.close()
        self._thread.join(timeout=1.0)
