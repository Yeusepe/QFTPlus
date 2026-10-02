#!/usr/bin/env python3
"""Read camera frames and resize them consistently for training and inference."""

from __future__ import annotations

from pathlib import Path

import cv2
import numpy as np

from capture_format import (
    FILE_HEADER,
    FILE_MAGIC,
    FRAME_HEADER,
    FRAME_MAGIC,
    TRANSPORT_HEADER,
)
from dataset_inspect import nearest_label_indices


def resize_cameras(strip: np.ndarray, size: int, count: int = 5) -> np.ndarray:
    """The same uint8/area resize for capture preparation and live inference."""
    if strip.ndim != 2 or strip.shape[0] != 400 or strip.shape[1] < count * 400:
        raise ValueError("Expected complete 400-pixel camera panels")
    return np.stack([
        cv2.resize(strip[:, i * 400:(i + 1) * 400], (size, size), interpolation=cv2.INTER_AREA)
        for i in range(count)
    ])


def scan_frames(path: Path, camera_mask: int = 0x1F) -> tuple[list[tuple[int, int, int, int]], int]:
    entries: list[tuple[int, int, int, int]] = []
    with path.open("rb") as capture:
        raw_file_header = capture.read(FILE_HEADER.size)
        if len(raw_file_header) != FILE_HEADER.size:
            raise ValueError("Capture file header is missing")
        fields = FILE_HEADER.unpack(raw_file_header)
        if fields[0] != FILE_MAGIC:
            raise ValueError("Unrecognized capture file")
        declared_frames = int(fields[5])
        while True:
            raw_frame_header = capture.read(FRAME_HEADER.size)
            if not raw_frame_header:
                break
            if len(raw_frame_header) != FRAME_HEADER.size:
                raise ValueError("Truncated capture frame header")
            magic, _record_size, _source_size, timestamp, _wall, raw_transport = (
                FRAME_HEADER.unpack(raw_frame_header)
            )
            if magic != FRAME_MAGIC:
                raise ValueError("Invalid frame record")
            transport = TRANSPORT_HEADER.unpack(raw_transport)
            width, height, stride, payload_size, mask = (
                int(transport[5]), int(transport[6]), int(transport[7]),
                int(transport[9]), int(transport[10]),
            )
            if mask != camera_mask or width != 400 * camera_mask.bit_count() or height != 400:
                raise ValueError(f"Training requires 400x400 cameras with mask 0x{camera_mask:x}")
            if stride != width or payload_size != width * height:
                raise ValueError("Unsupported frame payload layout")
            entries.append((capture.tell(), int(timestamp), width, height))
            capture.seek(payload_size, 1)
    if len(entries) != declared_frames:
        raise ValueError(
            f"Capture declares {declared_frames} frames but scanned {len(entries)}"
        )
    return entries, declared_frames
