"""Train candidate expression heads from explicit, masked five-camera labels.

The final capture round is never used for fitting or model selection. Metrics
are diagnostics, not a claim that attempted poses were correctly performed.
"""
from __future__ import annotations
import argparse
import json
import os
import random
from pathlib import Path
import numpy as np
import torch
from train_model import QuestProTrackingModel
from prepare_training import scan_frames, resize_cameras
from extra_face_capture import FAMILIES, target_names


def load_samples(captures: list[Path], size: int = 96):
    images, labels, groups = [], [], []
    known = {n for f in FAMILIES for n in target_names(f)}
    used_frames = set()
    for path in captures:
        session = json.loads(path.with_suffix(".qpsession.json").read_text())
        if session.get("sessionType") != "extra-face-stills-v1":
            raise ValueError("Only explicit extra-face labels are accepted")
        entries, _ = scan_frames(path)
        with path.open("rb") as stream:
            for sample in session["samples"]:
                if sample.get("excluded") or sample["promptIndex"] in session.get("skippedPrompts", []):
                    continue
                index = sample["frameIndex"]
                identity = (str(path.resolve()), index)
                if identity in used_frames:
                    raise ValueError("Duplicate frame in dataset")
                used_frames.add(identity)
                target = sample["targets"]
                if not target or not set(target) <= known or any(
                    not np.isfinite(v) or not 0 <= v <= 1 for v in target.values()
                ):
                    raise ValueError("Invalid or unsupported expression label")
                context = sample["context"].split("/repetition-")
                if len(context) != 2 or context[0] not in FAMILIES or context[1] not in ("0", "1", "2"):
                    raise ValueError("Missing independent repetition label")
                if not 0 <= index < len(entries):
                    raise ValueError("Frame label points outside capture")
                offset, _, width, height = entries[index]
                stream.seek(offset)
                raw = stream.read(width * height)
                if len(raw) != width * height:
                    raise ValueError("Truncated image")
                strip = np.frombuffer(raw, np.uint8).reshape(height, width)
                images.append(resize_cameras(strip, size))
                labels.append(target)
                groups.append(int(context[1]))
    if not images:
        raise ValueError("No usable labeled stills")
    names = sorted({key for item in labels for key in item})
    targets = np.array([[item.get(name, float("nan")) for name in names] for item in labels], np.float32)
    return np.stack(images), targets, np.asarray(groups), names


def evaluate(predictions, expected, names):
    result = {}
    for i, name in enumerate(names):
        valid = np.isfinite(expected[:, i])
        p, y = predictions[valid, i], expected[valid, i]
        neutral, full = y == 0, y == 1
        result[name] = {
            "count": len(y), "mae": float(np.abs(p-y).mean()) if len(y) else None,
            "neutralMaximum": float(p[neutral].max()) if neutral.any() else None,
            "fullMinimum": float(p[full].min()) if full.any() else None,
            "hasNeutralAndFull": bool(neutral.any() and full.any()),
        }
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--epochs", type=int, default=60)
    args = parser.parse_args()
    if args.epochs < 1 or args.output.exists():
        parser.error("epochs must be positive; output must be a new file")
    random.seed(42); np.random.seed(42); torch.manual_seed(42)
    images, targets, groups, names = load_samples(args.captures)
    train, holdout = groups < 2, groups == 2
    if train.sum() < 20 or holdout.sum() < 10:
        parser.error("Capture both training rounds and the separate final holdout round")
    for i, name in enumerate(names):
        for split in (train, holdout):
            values = targets[split, i]
            if not ((values == 0).sum() >= 3 and (values == 1).sum() >= 3):
                parser.error(f"{name} needs at least three neutral and full examples in each split; skipped poses need a new capture")
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model = QuestProTrackingModel(len(names)).to(device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=0.0008)
    x = torch.from_numpy(images[train]).float().to(device) / 255
    y = torch.from_numpy(targets[train]).to(device)
    print("TRAIN_STAGE index=1 total=1 name=expressions", flush=True)
    for epoch in range(args.epochs):
        if os.environ.get("QROOT_CONTEXT"):
            import qroot_ui
            qroot_ui.check_cancelled()
        print(f"TRAIN_EPOCH current={epoch+1} total={args.epochs}", flush=True)
        model.train()
        for ids in torch.randperm(len(x), device=device).split(32):
            if len(ids) < 2:
                continue
            batch = (x[ids] * random.uniform(0.92, 1.08) + random.uniform(-0.03, 0.03)).clamp(0, 1)
            predicted, _ = model(batch)
            mask = torch.isfinite(y[ids])
            loss = ((predicted[mask] - y[ids][mask]) ** 2).mean()
            optimizer.zero_grad(); loss.backward(); optimizer.step()
        if (epoch + 1) % 10 == 0:
            print(f"Epoch {epoch+1}/{args.epochs}, training MSE={loss.item():.4f}", flush=True)
    model.eval()
    with torch.inference_mode():
        predictions = torch.cat([model(batch.to(device).float()/255)[0].cpu() for batch in torch.from_numpy(images[holdout]).split(32)]).numpy()
    report = evaluate(predictions, targets[holdout], names)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    torch.save({"modelState": model.cpu().state_dict(), "expressionNames": names, "imageSize": 96,
                "schema": "extra-face-stills-v1", "validation": report,
                "approvedForOutput": False}, args.output)
    args.output.with_suffix(".validation.json").write_text(json.dumps({
        "trainFrames": int(train.sum()), "holdoutFrames": int(holdout.sum()), "targets": report,
        "status": "Research only. Review images and live held-out poses before enabling any override."
    }, indent=2))
    print(f"MODEL_READY path={args.output}", flush=True)


if __name__ == "__main__":
    main()
