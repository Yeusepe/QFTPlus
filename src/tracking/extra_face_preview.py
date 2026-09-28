"""Research model preview and explicitly approved, sparse expression output."""
import hashlib
import io
import json
import socket
import time
from pathlib import Path
import cv2
import numpy as np
import torch
from extra_face_capture import FAMILIES, STOCK_TRACKED, target_names
from inference_backend import prepare_inputs, prepare_model, select_device
from train_extra_face import FIVE_CAMERA, IMAGE_SIZE, create_model


def configured_models(config):
    keys = [("extraFaceModel" if family == "puff" else f"extraFaceModel-{family}", family) for family in FAMILIES]
    return [config[key] for key, family in keys
            if config.get(key) and config.get(f"extraFaceOutput-{family}", True) is not False]


class CombinedFaceModels(torch.nn.Module):
    def __init__(self, models):
        super().__init__()
        self.models = torch.nn.ModuleList(models)

    def forward(self, cameras):
        outputs = [model(cameras) for model in self.models]
        return torch.cat([expressions for expressions, _ in outputs], dim=1), outputs[0][1]


class ExtraFacePreview:
    def __init__(self, paths, enabled=False, *, render=True):
        paths = [Path(p).resolve() for p in ([paths] if isinstance(paths, (str, Path)) else paths)]
        if not paths:
            raise ValueError("No extra-face checkpoints")
        known = {n for f in FAMILIES for n in target_names(f)}
        selected = select_device("auto")
        device = torch.device("cpu" if selected == "directml" else selected)
        by_size, loaded = {}, set()
        for path in paths:
            checkpoint = torch.load(path, map_location=device, weights_only=True)
            names = list(checkpoint["expressionNames"])
            architecture = checkpoint.get("architecture", FIVE_CAMERA)
            if (checkpoint.get("schema") != "extra-face-stills-v1" or not set(names) <= known or set(names) & loaded
                    or architecture not in IMAGE_SIZE or checkpoint.get("imageSize") != IMAGE_SIZE[architecture]):
                raise ValueError("Not an extra-face research checkpoint, or two groups overlap: " + path.name)
            if enabled and checkpoint.get("approvedForOutput") is not True:
                raise ValueError("Checkpoint has not been reviewed and approved for output: " + path.name)
            model = create_model(architecture, len(names))
            model.load_state_dict(checkpoint["modelState"])
            by_size.setdefault(IMAGE_SIZE[architecture], []).append((model, names))
            loaded |= set(names)
        self.passes, self.expression_names = [], []
        for size, members in sorted(by_size.items()):
            combined = CombinedFaceModels([model for model, _ in members]).to(device).eval()
            key = paths[0]
            if selected == "directml":
                buffer = io.BytesIO()
                torch.save(combined.state_dict(), buffer)
                key = paths[0].parent / f"extra-face-combined-{hashlib.sha256(buffer.getvalue()).hexdigest()[:16]}.pt"
                if not key.exists():
                    key.write_bytes(buffer.getvalue())
            self.passes.append((size, prepare_model(combined, key, (1, 5, size, size), selected)))
            self.expression_names += [name for _, names in members for name in names]
        self.output = [name not in STOCK_TRACKED for name in self.expression_names]
        self.enabled = enabled
        self.render = render
        self.inference_ms = 0.
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    def update(self, strip):
        if strip.shape != (400, 2000):
            raise ValueError(f"Extra-face models require a 400x2000 five-camera strip, got {strip.shape}")
        started = time.perf_counter()
        with torch.inference_mode():
            parts = [model(prepare_inputs(model, strip, size, 5))[0][0].float().cpu().numpy() for size, model in self.passes]
        self.inference_ms = (time.perf_counter() - started) * 1000
        values = dict(zip(self.expression_names, map(float, np.concatenate(parts))))
        if not all(np.isfinite(v) and 0 <= v <= 1 for v in values.values()):
            raise ValueError("Invalid extra-face prediction")
        if self.enabled:
            sent = {name: value for (name, value), send in zip(values.items(), self.output) if send}
            self.socket.sendto(json.dumps({"version": 1, "enabled": True, "values": sent}).encode(), ("127.0.0.1", 27278))
        if not self.render:
            return None
        image = np.zeros((max(250, len(values)*25+90), 800, 3), np.uint8)
        title = "Extra expressions: OUTPUT ENABLED" if self.enabled else "Extra expressions: RESEARCH PREVIEW, no output"
        cv2.putText(image, f"{title}  ({self.inference_ms:.2f} ms)", (15, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (100, 220, 255), 1)
        for i, ((name, value), send) in enumerate(zip(values.items(), self.output)):
            y = 70+i*25
            cv2.putText(image, name + ("" if send else " (stock kept)"), (15, y), cv2.FONT_HERSHEY_SIMPLEX, 0.45, (225, 225, 225), 1)
            cv2.rectangle(image, (320, y-12), (320+round(value*440), y), (90, 220, 140), -1)
        return image

    def close(self):
        if self.enabled:
            self.socket.sendto(b'{"version":1,"enabled":false,"values":{}}', ("127.0.0.1", 27278))
        self.socket.close()
