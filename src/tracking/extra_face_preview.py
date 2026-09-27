"""Research model preview and explicitly approved, sparse expression output."""
import json
import os
import socket
import cv2
import numpy as np
import torch
from model_preview import LiveModelPreview
from extra_face_capture import FAMILIES, target_names


class ExtraFacePreview(LiveModelPreview):
    def __init__(self, path, enabled=False, *, render=True):
        super().__init__(path)
        checkpoint = torch.load(path, map_location="cpu", weights_only=True)
        known = {n for f in FAMILIES for n in target_names(f)}
        if checkpoint.get("schema") != "extra-face-stills-v1" or not set(self.expression_names) <= known:
            raise ValueError("Not an extra-face research checkpoint")
        self.enabled = enabled
        self.render = render
        if enabled and checkpoint.get("approvedForOutput") is not True:
            raise ValueError("Checkpoint has not been reviewed and approved for output")
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    def update(self, strip):
        prediction = self.predict(strip)
        values = dict(zip(self.expression_names, map(float, prediction.expressions)))
        if not all(np.isfinite(v) and 0 <= v <= 1 for v in values.values()):
            raise ValueError("Invalid extra-face prediction")
        if self.enabled:
            self.socket.sendto(json.dumps({"version": 1, "enabled": True, "values": values}).encode(), ("127.0.0.1", 27278))
        if os.environ.get("QROOT_CONTEXT"):
            import qroot_ui
            if qroot_ui.bridge is not None:
                qroot_ui.bridge.state.update(expressions=values)
        if not self.render:
            return None
        image = np.zeros((max(250, len(values)*25+90), 800, 3), np.uint8)
        title = "Extra expressions: OUTPUT ENABLED" if self.enabled else "Extra expressions: RESEARCH PREVIEW, no output"
        cv2.putText(image, title, (15, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (100, 220, 255), 1)
        for i, (name, value) in enumerate(values.items()):
            y = 70+i*25
            cv2.putText(image, f"{name}: {value:.2f}", (15, y), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (225, 225, 225), 1)
            cv2.rectangle(image, (320, y-12), (320+round(value*440), y), (90, 220, 140), -1)
        return image

    def close(self):
        if self.enabled:
            self.socket.sendto(b'{"version":1,"enabled":false,"values":{}}', ("127.0.0.1", 27278))
        self.socket.close()
