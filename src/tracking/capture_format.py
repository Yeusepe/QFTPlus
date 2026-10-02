"""Lossless, recoverable capture files for synchronized Quest Pro frames."""

from __future__ import annotations

import struct
import time
from pathlib import Path


FILE_MAGIC = b"QPCAP1\0\0"
FRAME_MAGIC = b"QPFRM1\0\0"
FILE_VERSION = 1
FILE_HEADER = struct.Struct("<8sIIQQQQ16s")
FRAME_HEADER = struct.Struct("<8sIIQQ64s")
TRANSPORT_HEADER = struct.Struct("<8sIIQQIIIIIIQ")


class CaptureWriter:
    """Append exact transport frames and finalize the file when capture ends."""

    def __init__(self, path: str | Path) -> None:
        self.path = Path(path).resolve()
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.created_wall_ns = time.time_ns()
        self.created_monotonic_ns = time.monotonic_ns()
        self.frame_count = 0
        self._file = self.path.open("xb", buffering=1024 * 1024)
        self._write_file_header(completed=False)

    def _write_file_header(self, completed: bool) -> None:
        self._file.seek(0)
        self._file.write(
            FILE_HEADER.pack(
                FILE_MAGIC,
                FILE_VERSION,
                FILE_HEADER.size,
                self.created_wall_ns,
                self.created_monotonic_ns,
                self.frame_count,
                int(completed),
                b"\0" * 16,
            )
        )
        self._file.seek(0, 2)

    def write(
        self,
        transport_header: bytes,
        payload: bytes,
        pc_monotonic_ns: int,
        pc_wall_ns: int,
    ) -> None:
        if len(transport_header) != TRANSPORT_HEADER.size:
            raise ValueError("Capture received an invalid transport header size")
        source_fields = TRANSPORT_HEADER.unpack(transport_header)
        if source_fields[9] != len(payload):
            raise ValueError("Capture payload size does not match its header")
        self._file.write(
            FRAME_HEADER.pack(
                FRAME_MAGIC,
                FRAME_HEADER.size,
                TRANSPORT_HEADER.size,
                pc_monotonic_ns,
                pc_wall_ns,
                transport_header,
            )
        )
        self._file.write(payload)
        self.frame_count += 1

    def close(self, completed: bool = True) -> None:
        if self._file.closed:
            return
        self._write_file_header(completed=completed)
        self._file.flush()
        self._file.close()
