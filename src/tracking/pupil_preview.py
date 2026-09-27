"""Conservative pupil ellipse diagnostics; pixel diameters are not millimeters."""
from dataclasses import dataclass
import cv2
import numpy as np


@dataclass(frozen=True)
class Pupil:
    ellipse: tuple
    diameter_px: float
    contrast: float


def _median_u8(pixels):
    cumulative = np.bincount(pixels, minlength=256).cumsum()
    a, b = cumulative.searchsorted([(pixels.size-1)//2+1, pixels.size//2+1])
    return (int(a)+int(b))*.5


def detect_pupil(gray: np.ndarray, *, center=None, diameter_range=None) -> Pupil | None:
    if gray.ndim != 2 or gray.dtype != np.uint8 or min(gray.shape) < 40:
        raise ValueError("Expected a grayscale uint8 eye image")
    smooth = cv2.GaussianBlur(gray, (5, 5), 0)
    height, width = gray.shape
    candidates = []
    otsu, _ = cv2.threshold(smooth, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)
    low, high = np.percentile(smooth, (2, 55))
    low = min(low, float(smooth.min()) + 2)
    kernel = np.ones((3, 3), np.uint8)
    opened = cv2.morphologyEx(smooth, cv2.MORPH_OPEN, kernel)
    for threshold in np.unique(np.r_[np.linspace(low, high, 28), otsu].round()):
        _, binary = cv2.threshold(opened, float(threshold), 255, cv2.THRESH_BINARY_INV)
        contours, _ = cv2.findContours(binary, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_NONE)
        for contour in contours:
            if len(contour) < 20:
                continue
            bx, by, bw, bh = cv2.boundingRect(contour)
            if bx < 8 or by < 8 or bx+bw >= width-8 or by+bh >= height-8:
                continue
            ellipse = cv2.fitEllipse(contour)
            (x, y), axes, angle = ellipse
            minor, major = sorted(axes)
            if not (10 <= minor <= major <= min(width, height) * 0.15 and minor / major >= 0.45):
                continue
            fill = cv2.contourArea(contour) / (np.pi * minor * major / 4)
            if not 0.90 <= fill <= 1.08:
                continue
            radius = int(np.ceil(major * .65)) + 3
            x0, y0 = max(0, int(x)-radius), max(0, int(y)-radius)
            patch = gray[y0:min(height, int(y)+radius+1), x0:min(width, int(x)+radius+1)]
            local = (x-x0, y-y0)
            inside = np.zeros_like(patch)
            outside = np.zeros_like(patch)
            cv2.ellipse(inside, (local, (axes[0]*0.85, axes[1]*0.85), angle), 255, -1)
            cv2.ellipse(outside, (local, (axes[0]*1.3, axes[1]*1.3), angle), 255, -1)
            cv2.ellipse(outside, (local, axes, angle), 0, -1)
            inner = patch[inside != 0]
            ring = patch[outside != 0]
            if not len(inner) or not len(ring):
                continue
            contrast = _median_u8(ring) - _median_u8(inner)
            if contrast < 20:
                continue
            if center is not None and np.linalg.norm(np.asarray((x, y))-center) > 25:
                continue
            candidates.append((contrast * fill, Pupil(ellipse, major, contrast)))
    pupils = []
    for score, pupil in candidates:
        x, y = pupil.ellipse[0]
        nested = any(other.diameter_px < pupil.diameter_px * .78
                     and np.linalg.norm(np.asarray(other.ellipse[0])-(x, y)) < pupil.diameter_px * .18
                     for _, other in candidates)
        if nested:
            continue
        if diameter_range is not None and not diameter_range[0] <= pupil.diameter_px <= diameter_range[1]:
            continue
        pupils.append((score, pupil))
    return max(pupils, key=lambda item: item[0])[1] if pupils else None
