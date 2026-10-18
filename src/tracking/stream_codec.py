"""Transmission quality for the PC camera stream: the relay's levels (relay.c), their decoding, and Automatic.

Small frames carry each camera area-resized to the PC model's 128 px with the relay's integer math (area_resize here
is the same math), plus the exact 25x25-pixel block sums the puff thumbnails need. Lossless and Data saver frames hold
gradient residuals (x - left - above + above-left, mod 256) that zlib Huffman-coded; Data saver first divides each
pixel by 5 (rounded), so its pixels are within 2 of the original.
"""
import zlib

import numpy as np

STRIP, SMALL, LOSSLESS, SAVER = range(4)
LEVELS = {"latency": SMALL, "quality": LOSSLESS, "saver": SAVER}
ONE_STRIP = 0x80
SMALL_SIDE, SMALL_PIXELS = 128, 5 * 128 * 128
SMALL_BYTES = SMALL_PIXELS + 2 * 256 * 4
FORMATS = {6: SMALL, 7: LOSSLESS, 8: SAVER}


def _area_matrix():
    weight = np.zeros((SMALL_SIDE, 400), np.float32)  # 1/8-pixel overlaps: each row sums to 25
    for o in range(SMALL_SIDE):
        for s in range(o * 25 // 8, min(400, (o * 25 + 24) // 8 + 1)):
            weight[o, s] = max(0, min(8 * s + 8, 25 * o + 25) - max(8 * s, 25 * o))
    return weight


AREA = _area_matrix()


def area_resize(strip):
    """400x2000 strip -> (5, 128, 128) uint8, as the relay and the model graph compute it (sums are exact in float32)."""
    cameras = np.asarray(strip, np.float32).reshape(400, 5, 400).transpose(1, 0, 2)
    sums = np.rint(AREA @ cameras @ AREA.T).astype(np.int64)
    return ((sums + 312) // 625).astype(np.uint8)


def decode(pixel_format, payload):
    """Small-frame payload -> cameras (5, 128, 128) uint8 and puff thumbnails (2, 256) float32 in 0..1."""
    level = FORMATS[pixel_format]
    data = bytes(payload) if level == SMALL else zlib.decompress(payload)
    if len(data) != SMALL_BYTES:
        raise ValueError("Small frame has the wrong size")
    cameras = np.frombuffer(data, np.uint8, SMALL_PIXELS).reshape(5, SMALL_SIDE, SMALL_SIDE)
    if level != SMALL:
        cameras = np.cumsum(np.cumsum(cameras, 2, dtype=np.uint8), 1, dtype=np.uint8)
    if level == SAVER:
        cameras = cameras * np.uint8(5)
    sums = np.frombuffer(data, "<u4", 512, SMALL_PIXELS).reshape(2, 256)
    return cameras, (sums / 159375.).astype(np.float32)


class Automatic:
    """Picks the level from how late frames arrive. Clocks differ, so lateness is the arrival delay above the lowest
    delay seen in the last 10 s; a queue building up means the connection is full. It starts at Latency first over USB
    and Quality over Wi-Fi, drops a level after 1 s of frames over 20 ms late, and tries a level up after a calm wait
    that doubles (up to 160 s) each time the try fails. A busy PC queues frames too, so lateness then doesn't count.
    ponytail: the wait never shrinks back within a session; reset it after a long calm stretch if that ever matters."""

    def __init__(self, usb):
        self.level = SMALL if usb else LOSSLESS
        self.delays, self.late_since, self.calm_since, self.wait, self.raised = [], None, None, 10., None

    def update(self, now, delay, busy=0.):
        """now, delay (arrival minus capture time, any fixed offset): seconds; busy: the receiver's busy share, 0..1."""
        self.delays = [(t, d) for t, d in self.delays if now - t < 10.] + [(now, delay)]
        late = delay - min(d for _, d in self.delays) > .02 and busy < .8
        if late:
            self.calm_since = None
            self.late_since = self.late_since if self.late_since is not None else now
            if now - self.late_since >= 1. and self.level < SAVER:
                if self.raised is not None and now - self.raised < 5.:
                    self.wait = min(self.wait * 2, 160.)
                self.level, self.late_since, self.raised, self.delays = self.level + 1, None, None, [(now, delay)]
        else:
            self.late_since = None
            self.calm_since = self.calm_since if self.calm_since is not None else now
            if now - self.calm_since >= self.wait and self.level > SMALL:
                self.level, self.calm_since, self.raised = self.level - 1, None, now
        return self.level
