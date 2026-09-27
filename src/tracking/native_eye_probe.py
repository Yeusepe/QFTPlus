
import cv2
import numpy as np


def put_text(
    image: np.ndarray,
    text: str,
    position: tuple[int, int],
    color: tuple[int, int, int] = (225, 225, 225),
    scale: float = 0.55,
    thickness: int = 1,
) -> None:
    cv2.putText(image, text, position, cv2.FONT_HERSHEY_SIMPLEX, scale,
                color, thickness, cv2.LINE_AA)


def gaze_panel(
    image: np.ndarray,
    origin: tuple[int, int],
    title: str,
    angles: tuple[float, float],
    color: tuple[int, int, int],
) -> None:
    x, y = origin
    width, height = 360, 250
    put_text(image, title, (x, y - 12), color, 0.62, 1)
    cv2.rectangle(image, (x, y), (x + width, y + height), (75, 75, 75), 1)
    cv2.line(image, (x + width // 2, y), (x + width // 2, y + height),
             (50, 50, 50), 1)
    cv2.line(image, (x, y + height // 2), (x + width, y + height // 2),
             (50, 50, 50), 1)
    yaw, pitch = angles
    marker_x = int(np.clip(x + (40.0 - yaw) / 80.0 * width, x, x + width))
    marker_y = int(np.clip(y + (35.0 - pitch) / 70.0 * height, y, y + height))
    cv2.circle(image, (marker_x, marker_y), 10, color, 2, cv2.LINE_AA)
    put_text(image, f"yaw {yaw:+.2f}  pitch {pitch:+.2f} deg",
             (x + 12, y + height - 14), color, 0.5)


def summary(values: list[float]) -> str:
    if not values:
        return "no samples"
    array = np.asarray(values, dtype=np.float64)
    return (
        f"n={len(array)} mean={np.mean(array):+.3f}  "
        f"sd={np.std(array):.3f}  span={np.ptp(array):.3f} deg"
    )
