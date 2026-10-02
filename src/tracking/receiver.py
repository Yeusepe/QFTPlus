#!/usr/bin/env python3
"""Receive Quest Pro inward-camera frames, run face tracking and Studio calibration, and serve camera snapshots."""

from __future__ import annotations

import http.server
import json
import os
import socket
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import cv2
import numpy as np

from capture_format import TRANSPORT_HEADER as HEADER
from headset import stop_event
from label_capture import LabelSidecarRecorder
from studio_capture import StudioCapture

MAGIC = b"QPLIVE3\0"
ROOT = Path(__file__).resolve().parent
STREAM_PORT, LABELS_PORT, PREVIEW_PORT = 27273, 27274, 8081


class ReceiverStopped(Exception):
    pass


def receive_exact(connection: socket.socket, size: int, should_stop) -> bytes:
    output = bytearray(size)
    view = memoryview(output)
    received = 0
    while received < size:
        if should_stop():
            raise ReceiverStopped()
        try:
            amount = connection.recv_into(view[received:])
        except socket.timeout:
            continue
        if amount == 0:
            raise ConnectionError("The headset streamer disconnected")
        received += amount
    return bytes(output)


class SharedPreview:
    """Latest camera panels for the Studio's Cameras page, JPEG-encoded on request."""

    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.images: dict[str, np.ndarray] = {}
        self.jpegs: dict[str, bytes] = {}
        self.pupils = [None, None]
        self.pupil_requested_at = -float("inf")

    def update(self, strip: np.ndarray, pupils) -> None:
        images = {f"camera{i}": strip[:, i * 400:(i + 1) * 400] for i in range(5)}
        images["pupil0"], images["pupil1"] = images["camera0"], images["camera1"]
        with self.lock:
            self.images, self.jpegs, self.pupils = images, {}, list(pupils)

    def get_jpeg(self, key: str) -> bytes | None:
        with self.lock:
            if key in ("pupil0", "pupil1"):
                self.pupil_requested_at = time.monotonic()
            if key in self.jpegs:
                return self.jpegs[key]
            image = source = self.images.get(key)
            pupil = self.pupils[int(key[-1])] if key in ("pupil0", "pupil1") else None
        if image is None:
            return None
        if key in ("pupil0", "pupil1"):
            image = cv2.cvtColor(image, cv2.COLOR_GRAY2BGR)
            if pupil is not None:
                cv2.ellipse(image, pupil.ellipse, (70, 220, 130), 2)
            cv2.putText(image, f"{pupil.diameter_px:.1f} px" if pupil else "Uncertain", (12, 28),
                        cv2.FONT_HERSHEY_SIMPLEX, .65, (70, 220, 130) if pupil else (90, 190, 255), 1)
        ok, encoded = cv2.imencode(".jpg", image, [cv2.IMWRITE_JPEG_QUALITY, 85])
        if not ok:
            return None
        jpeg = encoded.tobytes()
        with self.lock:
            if self.images.get(key) is source:
                self.jpegs[key] = jpeg
        return jpeg


class PreviewHandler(http.server.BaseHTTPRequestHandler):
    shared: SharedPreview

    def do_GET(self) -> None:
        authorities = {f"127.0.0.1:{self.server.server_port}", f"localhost:{self.server.server_port}"}
        host = self.headers.get("Host", "").lower()
        origin = self.headers.get("Origin")
        if (host not in authorities
                or self.headers.get("Sec-Fetch-Site", "none") not in {"none", "same-origin"}
                or (origin is not None and origin != f"http://{host}")):
            self.send_error(403)
            return
        jpeg = self.shared.get_jpeg(self.path[1:-4]) if self.path.endswith(".jpg") else None
        if jpeg is None:
            self.send_error(404)
            return
        self.send_response(200)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Type", "image/jpeg")
        self.send_header("Content-Length", str(len(jpeg)))
        self.end_headers()
        try:
            self.wfile.write(jpeg)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def log_message(self, _format: str, *_args: object) -> None:
        return

    def end_headers(self) -> None:
        self.send_header("Cross-Origin-Resource-Policy", "same-origin")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        super().end_headers()


def start_preview_server(shared: SharedPreview, port: int) -> http.server.ThreadingHTTPServer:
    handler = type("QuestProPreviewHandler", (PreviewHandler,), {"shared": shared})
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)
    threading.Thread(target=server.serve_forever, name="preview-server", daemon=True).start()
    return server


def studio_config():
    return json.loads((Path(os.environ['APPDATA'])/'VRCFaceTracking/QproAutoStart.json').read_text(encoding='utf-8-sig'))


def face_log_path(config, kind):
    """<researchDataPath>/sessions/<date>-<kind>.facelog.jsonl when 'Save face-tracking logs' is on, else None."""
    if not config.get('faceLog'):
        return None
    root = Path(config.get('researchDataPath') or Path(os.environ.get('LOCALAPPDATA', '.')) / 'QFTPlus' / 'research')
    return str(root / 'sessions' / (time.strftime('%Y%m%d-%H%M%S') + f'-{kind}.facelog.jsonl'))


def load_models():
    """The settings' face model and pupil output; a face model that can't load is reported, not fatal."""
    from pupil_dilation import PupilDilation
    from tongue_output import TongueBroadcaster
    from universal_face import FAMILIES, UniversalFace
    config = studio_config()
    face, error = None, ''
    tongue = TongueBroadcaster(enabled=config.get('tongueOutput', True), supported=["extension", "horizontal", "vertical"])
    try:
        face = UniversalFace(config.get('universalModelPath') or ROOT / 'models' / 'universal-face-v2.npz', config.get('faceEnrollment'),
            config.get('extraFaceOutput', True) is not False,
            families=[f for f in FAMILIES if config.get(f'extraFaceOutput-{f}', True) is not False],
            bias=float(config.get('faceEventBias') or 0), log=face_log_path(config, 'universal'), tongue=tongue)
    except Exception as failure:
        print('UNIVERSAL_FAILED ' + str(failure), flush=True)
        error = str(failure)
        tongue.close()
    pupils = PupilDilation(path=ROOT / 'calibration' / 'qpro-pupil-dilation.json', enabled=config.get('pupilDilation', False), asynchronous=True)
    return face, pupils, error


def connect(should_stop) -> socket.socket | None:
    deadline = time.monotonic() + 20.0
    while not should_stop():
        try:
            connection = socket.create_connection((os.environ.get("QFT_STREAM_HOST", "127.0.0.1"), STREAM_PORT), timeout=2)
            break
        except OSError:
            if time.monotonic() >= deadline:
                raise ConnectionError("The injected streamer did not begin listening. Send the three logs.")
            time.sleep(0.25)
    else:
        return None
    connection.settimeout(0.25)
    connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    connection.sendall(os.environ["QFT_STREAM_TOKEN"].encode("ascii"))
    return connection


def main() -> int:
    cv2.setNumThreads(1)
    token = os.environ.get("QFT_STREAM_TOKEN", "")
    if len(token) != 64 or set(token) - set("0123456789abcdef"):
        raise SystemExit("Start tracking from QFT+.")
    should_stop = stop_event().is_set
    shared = SharedPreview()
    server = start_preview_server(shared, PREVIEW_PORT)
    labels = LabelSidecarRecorder(port=LABELS_PORT)
    studio = StudioCapture(ROOT)
    loader = ThreadPoolExecutor(max_workers=1, thread_name_prefix="studio-reload")
    loading = None
    face = pupils = connection = None
    try:
        connection = connect(should_stop)
        if connection is None:
            return 0
        print("CONNECTED", flush=True)
        while True:
            raw_header = receive_exact(connection, HEADER.size, should_stop)
            (magic, version, header_size, _sequence, _timestamp_ns, width, height,
             stride, pixel_format, payload_size, camera_mask, _rejected_torn) = HEADER.unpack(raw_header)
            if magic != MAGIC or version != 3 or header_size != HEADER.size:
                raise ValueError("Unexpected live-stream header")
            if camera_mask != 0x1F or (width, height, stride, pixel_format, payload_size) != (2000, 400, 2000, 1, 800_000):
                raise ValueError(f"Unsupported frame layout {width}x{height}, cameras 0x{camera_mask:x}, format {pixel_format}")
            payload = receive_exact(connection, payload_size, should_stop)
            now_ns = time.monotonic_ns()
            strip = np.frombuffer(payload, dtype=np.uint8).reshape((height, stride))
            shared.update(strip, pupils.detected_pupils if pupils else (None, None))
            try:
                studio.update(strip, raw_header, payload, now_ns, labels)
            except Exception as error:
                print("STUDIO_CAPTURE_ERROR " + str(error), flush=True)
                try:
                    studio.close()
                except OSError:
                    pass
                studio.state.update(phase="cancelled", completed=False, error=str(error))
            if studio.reload_requested and loading is None:
                studio.reload_requested = False
                loading = loader.submit(load_models)
            if loading is not None and loading.done():
                try:
                    new_face, new_pupils, error = loading.result()
                    for old in (face, pupils):
                        if old is not None:
                            old.close()
                    face, pupils = new_face, new_pupils
                    studio.state['error'] = error
                except Exception as error:
                    studio.state['error'] = 'Could not load calibration: ' + str(error)
                loading = None
            if pupils is not None:
                pupils.update(strip, preview=time.monotonic() - shared.pupil_requested_at < 1.)
            if face is not None:
                face.update(strip, labels.nearest_sample(now_ns), labels.schema_names, now_ns)
    except ReceiverStopped:
        pass
    finally:
        studio.close()
        loader.shutdown(wait=False, cancel_futures=True)
        if connection is not None:
            connection.close()
        server.shutdown()
        server.server_close()
        for model in (face, pupils):
            if model is not None:
                model.close()
        labels.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
