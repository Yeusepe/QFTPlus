"""Train candidate expression heads from explicit, masked five-camera labels.

The final capture round is never used for fitting or model selection. Metrics
are diagnostics, not a claim that attempted poses were correctly performed.
"""
from __future__ import annotations
import argparse
import json
import random
from pathlib import Path
import hashlib
import numpy as np
import torch
from train_model import QuestProTrackingModel
from train_tongue_model import SpatialTongueEncoder
from prepare_training import scan_frames, resize_cameras
from extra_face_capture import FAMILIES, target_names
import gpu_training

FIVE_CAMERA, MOUTH = "five-camera-v1", "ava-mouth-v1"
IMAGE_SIZE = {FIVE_CAMERA: 96, MOUTH: 128}
ENCODER = Path(__file__).resolve().parent / "models" / "ava-mouth-encoder.pt"
MOUTH_EPOCHS, MOUTH_STEPS_PER_EPOCH = 40, 10
MOUTH_LAST_STAGE = True


class MouthExpressionModel(torch.nn.Module):

    def __init__(self, expression_count: int) -> None:
        super().__init__()
        self.encoder = SpatialTongueEncoder()
        self.head = torch.nn.Linear(320, expression_count)

    def forward(self, cameras):
        n, _, h, w = cameras.shape
        mouth = (cameras[:, 2:4].reshape(n * 2, 1, h, w) - .25) / .25
        features = self.encoder(mouth).mean((2, 3)).reshape(n, -1)
        return torch.sigmoid(self.head(features)), cameras.new_zeros(n, 8)


def create_model(architecture, count):
    return MouthExpressionModel(count) if architecture == MOUTH else QuestProTrackingModel(count)


def labelled_names(captures):
    return {name for path in captures
            for sample in json.loads(path.with_suffix(".qpsession.json").read_text())["samples"]
            for name in sample["targets"]}


def architecture_for(names, encoder):
    if encoder is None or not Path(encoder).exists() or any(n.startswith("Brow") for n in names):
        return FIVE_CAMERA
    return MOUTH


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
            "halfMean": float(p[y == .5].mean()) if (y == .5).any() else None,
        }
        opposite = name.removesuffix("Left") + "Right" if name.endswith("Left") else name.removesuffix("Right") + "Left"
        if opposite in names:
            other = expected[valid, names.index(opposite)]
            isolated, inactive = (y == 1) & (other == 0), (y == 0) & (other == 1)
            result[name].update(
                isolatedFullMinimum=float(p[isolated].min()) if isolated.any() else None,
                oppositeOnlyMaximum=float(p[inactive].max()) if inactive.any() else None)
    return result


def expression_loss(predicted, target, names):
    losses = []
    for i in range(len(names)):
        valid = torch.isfinite(target[:, i])
        p, y = predicted[valid, i], target[valid, i]
        if not len(y):
            continue
        losses.append(torch.stack([(p[y == level] - level).square().mean()
                                   for level in y.unique()]).mean())
        gap = y[:, None] - y[None, :]
        ordered = gap > 0
        if ordered.any():
            separation = p[:, None] - p[None, :]
            losses.append(.25 * (gap[ordered] - separation[ordered]).clamp(min=0).square().mean())
    for i, name in enumerate(names):
        opposite = name.removesuffix("Left") + "Right"
        if name.endswith("Left") and opposite in names:
            j = names.index(opposite)
            valid = torch.isfinite(target[:, i]) & torch.isfinite(target[:, j])
            if valid.any():
                delta = predicted[valid, i] - predicted[valid, j]
                losses.append(.5 * (delta - (target[valid, i] - target[valid, j])).square().mean())
    return torch.stack(losses).sum()


def unrelated_cameras(names):
    return [2, 3] if any(name.startswith("Brow") for name in names) else [0, 1, 4]


def mix_cheeks(images, targets, names):
    if not all(name.startswith("Cheek") for name in names):
        return images, targets
    chosen = (torch.rand(len(images), device=images.device) < .3).nonzero().squeeze(1)
    partner = chosen[torch.randperm(len(chosen), device=images.device)]
    right = [i for i, name in enumerate(names) if name.endswith("Right")]
    images, targets = images.clone(), targets.clone()
    images[chosen, 3] = images[partner, 3]
    targets[chosen[:, None], right] = targets[partner[:, None], right]
    return images, targets


def augment(images, unrelated=()):
    n, cameras = images.shape[:2]
    device = images.device
    angle = torch.empty(n, device=device).uniform_(-.07, .07)
    scale = torch.empty(n, device=device).uniform_(.94, 1.06)
    theta = torch.zeros(n, 2, 3, device=device)
    theta[:, 0, 0] = theta[:, 1, 1] = scale * angle.cos()
    theta[:, 0, 1], theta[:, 1, 0] = -scale * angle.sin(), scale * angle.sin()
    theta[:, :, 2] = torch.empty(n, 2, device=device).uniform_(-.05, .05)
    grid = torch.nn.functional.affine_grid(theta, images.shape, align_corners=False)
    shifted = torch.nn.functional.grid_sample(images, grid, padding_mode="border", align_corners=False)
    contrast = torch.empty(n, cameras, 1, 1, device=device).uniform_(.85, 1.15)
    brightness = torch.empty_like(contrast).uniform_(-.05, .05)
    shifted = (shifted * contrast + brightness).clamp(0, 1)
    if unrelated:
        shifted[:, unrelated] = shifted[torch.randperm(n, device=device)][:, unrelated]
    return shifted


def nuisance(images):
    out = images.clone()
    x = images[:, 2:4].reshape(-1, 1, *images.shape[2:])
    n, device = x.shape[0], x.device
    zoom = torch.empty(n, device=device).uniform_(.80, 1.20)
    angle = torch.empty(n, device=device).uniform_(-.17, .17)
    theta = torch.zeros(n, 2, 3, device=device)
    theta[:, 0, 0] = theta[:, 1, 1] = angle.cos() / zoom
    theta[:, 0, 1], theta[:, 1, 0] = -angle.sin() / zoom, angle.sin() / zoom
    theta[:, :, 2] = torch.empty(n, 2, device=device).uniform_(-.10, .10)
    grid = torch.nn.functional.affine_grid(theta, x.shape, align_corners=False)
    x = torch.nn.functional.grid_sample(x, grid, padding_mode="border", align_corners=False)
    gamma, gain = (torch.empty(n, 1, 1, 1, device=device).uniform_(*r) for r in ((.7, 1.4), (.6, 1.4)))
    x = x.clamp_min(1e-4).pow(gamma) * gain + torch.empty(n, 1, 1, 1, device=device).uniform_(-.08, .08)
    blur = torch.rand(n, device=device) < .3
    if blur.any():
        x[blur] = torch.nn.functional.avg_pool2d(x[blur], 3, 1, 1, count_include_pad=False)
    x = x + torch.randn_like(x) * torch.empty(n, 1, 1, 1, device=device).uniform_(0, .03)
    out[:, 2:4] = x.clamp(0, 1).reshape(-1, 2, *images.shape[2:])
    return out


def predict(model, images, device):
    model.eval()
    with torch.inference_mode():
        return torch.cat([model(batch.to(device).float()/255)[0].cpu()
                          for batch in torch.from_numpy(images).split(32)]).numpy()


def fit(images, targets, names, epochs, device, validation=None, stage=1, architecture=FIVE_CAMERA, encoder=None):
    options = dict(validation=validation, stage=stage, architecture=architecture, encoder=encoder)
    if architecture == MOUTH and device.type == "cpu" and gpu_training.enabled():
        try:
            return _fit(images, targets, names, epochs, device, gpu=True, **options)
        except Exception as reason:
            print(f"TRAIN_ENGINE cpu reason={reason!r}", flush=True)
    return _fit(images, targets, names, epochs, device, **options)


def _fit(images, targets, names, epochs, device, validation=None, stage=1, architecture=FIVE_CAMERA, encoder=None, gpu=False):
    random.seed(42); np.random.seed(42); torch.manual_seed(42)
    model = create_model(architecture, len(names)).to(device)
    mouth = architecture == MOUTH
    if mouth:
        model.encoder.load_state_dict(torch.load(encoder, map_location="cpu", weights_only=True)["encoderState"])
        frozen = model.encoder.network[:-4] if MOUTH_LAST_STAGE else torch.nn.Sequential()
        frozen.requires_grad_(False)
        optimizer = torch.optim.AdamW([p for p in model.parameters() if p.requires_grad], lr=.001, weight_decay=.05)
        schedule = None
    else:
        optimizer = torch.optim.AdamW(model.parameters(), lr=.0005, weight_decay=.05)
        schedule = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, epochs)
    engine = None
    if gpu:
        engine = gpu_training.MouthEngine(model, names, 32, 3 if MOUTH_LAST_STAGE else 0, .001, .05, IMAGE_SIZE[MOUTH])
        print(f"TRAIN_ENGINE gpu compile={engine.trainer.init['compileSeconds']:.1f}s", flush=True)
        estimate = engine.predict
    else:
        estimate = lambda values: predict(model, values, device)
    unrelated = unrelated_cameras(names)
    x = torch.from_numpy(images).float().to(device) / 255
    y = torch.from_numpy(targets).to(device)
    best_score, best_epoch = float("inf"), 0
    print(f"TRAIN_STAGE index={stage} total=2 name={'selection' if validation is not None else 'expressions'}", flush=True)
    for epoch in range(epochs):
        print(f"TRAIN_EPOCH current={epoch+1} total={epochs}", flush=True)
        model.train()
        if mouth:
            frozen.eval()
            for _ in range(MOUTH_STEPS_PER_EPOCH):
                ids = torch.randint(0, len(x), (32,), device=device)
                if engine is not None:
                    engine.step(nuisance(x[ids]), y[ids], expression_loss)
                    continue
                loss = expression_loss(model(nuisance(x[ids]))[0], y[ids], names)
                optimizer.zero_grad(); loss.backward(); optimizer.step()
        else:
            for ids in torch.randperm(len(x), device=device).tensor_split(max(1, (len(x)+31)//32)):
                batch, labels = mix_cheeks(x[ids], y[ids], names)
                predicted, _ = model(augment(batch, unrelated))
                loss = expression_loss(predicted, labels, names)
                optimizer.zero_grad(); loss.backward(); optimizer.step()
            schedule.step()
        if validation is not None:
            estimated = estimate(validation[0])
            error = np.abs(estimated - validation[1])
            score = float(np.nanmean(error) + .25*np.nanmax(error))
            if np.isfinite(score) and score < best_score:
                best_score, best_epoch = score, epoch+1
        if (epoch+1) % 10 == 0 or epoch+1 == epochs:
            mse = float(np.nanmean((estimate(images)-targets)**2))
            detail = f", validation MAE={np.nanmean(error):.4f}, worst={np.nanmax(error):.4f}" if validation is not None else ""
            print(f"Epoch {epoch+1}/{epochs}, training MSE={mse:.4f}{detail}", flush=True)
    if engine is not None:
        engine.sync()
        engine.close()
    if validation is not None and not best_epoch:
        raise ValueError("Training produced no finite validation score")
    return model, best_epoch


def develop(images, targets, names, train, validation, device, architecture=FIVE_CAMERA, encoder=None, epochs=None):
    options = dict(architecture=architecture, encoder=encoder)
    development = train | validation
    _, best_epoch = fit(images[train], targets[train], names, epochs or (MOUTH_EPOCHS if architecture == MOUTH else 60), device,
                        validation=(images[validation], targets[validation]), **options)
    print(f"TRAIN_SELECTED epoch={best_epoch} validationRound=2 testRound=3", flush=True)
    model, _ = fit(images[development], targets[development], names, best_epoch, device, stage=2, **options)
    return model, best_epoch


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--epochs", type=int, help="default 60, or 40 for the pretrained mouth model")
    parser.add_argument("--encoder", type=Path, default=ENCODER,
                        help="pretrained mouth encoder; without it every group uses the five-camera model")
    args = parser.parse_args()
    architecture = architecture_for(labelled_names(args.captures), args.encoder)
    args.epochs = args.epochs or (MOUTH_EPOCHS if architecture == MOUTH else 60)
    if args.epochs < 1 or args.output.exists():
        parser.error("epochs must be positive; output must be a new file")
    print(f"TRAIN_ARCHITECTURE {architecture}", flush=True)
    images, targets, groups, names = load_samples(args.captures, IMAGE_SIZE[architecture])
    train, validation, holdout = groups == 0, groups == 1, groups == 2
    if min(train.sum(), validation.sum(), holdout.sum()) < 10:
        parser.error("Capture both training rounds and the separate final holdout round")
    keep = [i for i in range(len(names)) if all((targets[split, i] == 0).sum() >= 3 and (targets[split, i] == 1).sum() >= 3
                                                  for split in (train, validation, holdout))]
    for i in sorted(set(range(len(names))) - set(keep)):
        print(f"TRAIN_SKIPPED {names[i]}", flush=True)
    if not keep:
        parser.error("Every pose was skipped, so there's nothing to calibrate.")
    targets, names = targets[:, keep], [names[i] for i in keep]
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    model, best_epoch = develop(images, targets, names, train, validation, device, architecture, args.encoder, args.epochs)
    development = train | validation
    predictions = predict(model, images[holdout], device)
    report = evaluate(predictions, targets[holdout], names)
    for name, metrics in report.items():
        print(f"HOLDOUT {name}: {json.dumps(metrics)}", flush=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    provenance = {"pretrainedEncoder": {"sha256": hashlib.sha256(args.encoder.read_bytes()).hexdigest(),
                                        "data": "Ava-256 (CC BY-NC 4.0)"}} if architecture == MOUTH else {}
    torch.save({"modelState": model.cpu().state_dict(), "expressionNames": names,
                "architecture": architecture, "imageSize": IMAGE_SIZE[architecture], **provenance,
                "schema": "extra-face-stills-v1", "validation": report,
                "selectedEpoch": best_epoch, "trainingMethod": "balanced-ordinal-sides-v1",
                "approvedForOutput": False}, args.output)
    args.output.with_suffix(".validation.json").write_text(json.dumps({
        "trainFrames": int(development.sum()), "selectionFrames": int(validation.sum()),
        "selectedEpoch": best_epoch, "holdoutFrames": int(holdout.sum()), "targets": report,
        "status": "Research only. Review images and live held-out poses before enabling any override."
    }, indent=2))
    print(f"MODEL_READY path={args.output}", flush=True)


if __name__ == "__main__":
    main()
