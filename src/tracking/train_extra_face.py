"""Train candidate expression heads from explicit, masked five-camera labels.

Rounds 1-2 train a check model that is scored on the untouched final round;
the saved model then uses the same fixed recipe on every round. Metrics are
diagnostics, not a claim that attempted poses were correctly performed.
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

FIVE_CAMERA, MOUTH, SIDES = "five-camera-v1", "ava-mouth-v1", "ava-mouth-sides-v1"
IMAGE_SIZE = {FIVE_CAMERA: 96, MOUTH: 128, SIDES: 128}
ENCODER = Path(__file__).resolve().parent / "models" / "ava-mouth-encoder.pt"
MOUTH_EPOCHS, MOUTH_STEPS_PER_EPOCH = 40, 10


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


class MouthSidesModel(MouthExpressionModel):
    """One shared head per cheek shape; each side's value reads only that side's mouth camera.

    Trained as MouthExpressionModel on side_rows, so the weights are identical. Outputs follow
    sorted names: shape0 Left, shape0 Right, shape1 Left, ...
    """

    def forward(self, cameras):
        n, _, h, w = cameras.shape
        f = self.encoder((cameras[:, 2:4].reshape(n * 2, 1, h, w) - .25) / .25).mean((2, 3)).reshape(n, 2, -1)
        sides = [torch.sigmoid(self.head(torch.cat([f[:, s], f[:, s]], 1))) for s in (0, 1)]
        return torch.stack(sides, 2).flatten(1), cameras.new_zeros(n, 8)


def create_model(architecture, count):
    if architecture == SIDES:
        return MouthSidesModel(count // 2)
    return MouthExpressionModel(count) if architecture == MOUTH else QuestProTrackingModel(count)


def sided_cheeks(names):
    return bool(names) and all(n.startswith("Cheek") and n.endswith(("Left", "Right")) for n in names)


def labelled_names(captures):
    return {name for path in captures
            for sample in json.loads(path.with_suffix(".qpsession.json").read_text())["samples"]
            for name in sample["targets"]}


def architecture_for(names, encoder):
    if encoder is None or not Path(encoder).exists() or any(n.startswith("Brow") for n in names):
        return FIVE_CAMERA
    return SIDES if sided_cheeks(names) else MOUTH


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


NEVER_PUFF = ("tongue", "brows", "pucker", "corners", "nose", "jaw", "mouth")


def never_puff_captures(capture: Path) -> list[Path]:
    """The user's completed tongue and other-group calibrations beside this one: none of their stills is a cheek puff."""
    found = []
    for path in sorted(capture.parent.glob("*.qpcap")):
        try:
            session = json.loads(path.with_suffix(".qpsession.json").read_text())
        except (OSError, ValueError):
            continue
        if (path.name.split("-", 1)[0] in NEVER_PUFF and session.get("completed")
                and session.get("sessionType") in ("extra-face-stills-v1", "tongue-stereo-refinement-v2")):
            found.append(path)
    return found


def load_never_puff(captures: list[Path], count: int, size: int) -> np.ndarray:
    """Up to `count` stills spread evenly over the captures, as five-camera stacks.

    Tongue captures hold cameras 2-4 only, so cameras 0-1 stay blank; the mouth models never read them.
    """
    stills = []
    for path in captures:
        try:
            session = json.loads(path.with_suffix(".qpsession.json").read_text())
            tongue = session["sessionType"] != "extra-face-stills-v1"
            entries, _ = scan_frames(path, 0x1C if tongue else 0x1F)
            stills += [(path, tongue, entries[s["frameIndex"]]) for s in session["samples"]
                       if not s.get("excluded") and s["promptIndex"] not in session.get("skippedPrompts", [])
                       and 0 <= s["frameIndex"] < len(entries)]
        except (OSError, ValueError, KeyError) as error:
            print(f"TRAIN_NEGATIVES skipped={path.name} reason={error!r}", flush=True)
    images = []
    for k in np.unique(np.linspace(0, len(stills) - 1, min(count, len(stills))).round().astype(int)) if stills else []:
        path, tongue, (offset, _, width, height) = stills[k]
        with path.open("rb") as stream:
            stream.seek(offset)
            raw = stream.read(width * height)
        try:
            cameras = resize_cameras(np.frombuffer(raw, np.uint8).reshape(height, width), size, 3 if tongue else 5)
        except ValueError:
            continue
        images.append(np.concatenate([np.zeros((2, size, size), np.uint8), cameras]) if tongue else cameras)
    return np.stack(images) if images else np.zeros((0, 5, size, size), np.uint8)


def spearman(predicted, expected):
    """Rank correlation with averaged ties; None when the labels have no order to check."""
    if len(np.unique(expected)) < 2:
        return None
    ranks = []
    for values in (predicted, expected):
        _, inverse, counts = np.unique(values, return_inverse=True, return_counts=True)
        ranks.append((counts.cumsum() - (counts + 1) / 2)[inverse])
    return 0.0 if ranks[0].std() == 0 else float(np.corrcoef(*ranks)[0, 1])


def evaluate(predictions, expected, names):
    """Holdout report on reliable labels: relaxed, full and the other side's pose.

    Halfway prompts are performed inconsistently, and when one-sided poses exist the both-sides
    poses are too (the air splits between the cheeks), so those are reported but not gated.
    """
    result = {}
    for i, name in enumerate(names):
        valid = np.isfinite(expected[:, i])
        p, y = predictions[valid, i], expected[valid, i]
        opposite = name.removesuffix("Left") + "Right" if name.endswith("Left") else name.removesuffix("Right") + "Left"
        other = np.nan_to_num(expected[valid, names.index(opposite)]) if opposite in names else np.zeros_like(y)
        isolated = ((y == 1) & (other == 0)).any()
        kept = ~((y > 0) & (other > 0)) if isolated else np.ones(len(y), bool)
        relaxed, full, leak = (y == 0) & (other == 0), (y == 1) & kept, (y == 0) & (other == 1)
        reliable = ((y == 0) | (y == 1)) & kept
        ordered = kept & (other == 0) if isolated else kept
        result[name] = {
            "count": len(y), "mae": float(np.abs(p - y).mean()) if len(y) else None,
            "reliableMae": float(np.abs(p - y)[reliable].mean()) if reliable.any() else None,
            "relaxedP90": float(np.percentile(p[relaxed], 90)) if relaxed.any() else None,
            "fullP10": float(np.percentile(p[full], 10)) if full.any() else None,
            "leakP90": float(np.percentile(p[leak], 90)) if leak.any() else None,
            "spearman": spearman(p[ordered], y[ordered]),
            "hasNeutralAndFull": bool(relaxed.any() and full.any()),
            "halfMean": float(p[y == .5].mean()) if (y == .5).any() else None,
        }
    return result


def expression_loss(predicted, target, names, soft=None):
    """Balanced level loss + ordinal hinge + left/right difference; soft targets only keep their order."""
    soft = torch.zeros_like(target, dtype=torch.bool) if soft is None else soft
    losses = []
    for i in range(len(names)):
        valid = torch.isfinite(target[:, i])
        p, y, s = predicted[valid, i], target[valid, i], soft[valid, i]
        if not len(y):
            continue
        losses.append(torch.stack([(p[(y == level) & ~s] - level).square().sum() / (y == level).sum()
                                   for level in y.unique()]).mean())
        gap = y[:, None] - y[None, :]
        ordered = gap > 0
        if ordered.any():
            gap = torch.where(s[:, None] | s[None, :], gap.clamp(max=.1), gap)
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


def side_rows(images, targets, names):
    """Each still becomes a left row and a right row whose two mouth views both show that side's camera.

    Returns rows, per-shape targets, the ordering-only mask (one-sided halfway) and the shape names.
    """
    shapes = sorted({name.removesuffix("Left").removesuffix("Right") for name in names})
    left = targets[:, [names.index(s + "Left") for s in shapes]]
    right = targets[:, [names.index(s + "Right") for s in shapes]]
    left_rows, right_rows = images.copy(), images.copy()
    left_rows[:, 3], right_rows[:, 2] = images[:, 2], images[:, 3]
    own, other = np.concatenate([left, right]), np.concatenate([right, left])
    return np.concatenate([left_rows, right_rows]), own, (own == .5) & (other == 0), shapes


def predict(model, images, device):
    model.eval()
    with torch.inference_mode():
        return torch.cat([model(batch.to(device).float()/255)[0].cpu()
                          for batch in torch.from_numpy(images).split(32)]).numpy()


def fit(images, targets, names, epochs, device, stage=1, architecture=FIVE_CAMERA, encoder=None, soft=None):
    options = dict(stage=stage, architecture=architecture, encoder=encoder, soft=soft)
    if architecture == MOUTH and device.type == "cpu" and gpu_training.enabled():
        try:
            return _fit(images, targets, names, epochs, device, gpu=True, **options)
        except Exception as reason:
            print(f"TRAIN_ENGINE cpu reason={reason!r}", flush=True)
    return _fit(images, targets, names, epochs, device, **options)


def _fit(images, targets, names, epochs, device, stage=1, architecture=FIVE_CAMERA, encoder=None, soft=None, gpu=False):
    random.seed(42); np.random.seed(42); torch.manual_seed(42)
    model = create_model(architecture, len(names)).to(device)
    mouth = architecture == MOUTH
    if mouth:
        model.encoder.load_state_dict(torch.load(encoder, map_location="cpu", weights_only=True)["encoderState"])
        frozen = model.encoder.network[:-4]
        frozen.requires_grad_(False)
        optimizer = torch.optim.AdamW([p for p in model.parameters() if p.requires_grad], lr=.001, weight_decay=.05)
        schedule = None
    else:
        optimizer = torch.optim.AdamW(model.parameters(), lr=.0005, weight_decay=.05)
        schedule = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, epochs)
    engine = None
    if gpu:
        engine = gpu_training.MouthEngine(model, names, 32, 3, .001, .05, IMAGE_SIZE[MOUTH])
        print(f"TRAIN_ENGINE gpu compile={engine.trainer.init['compileSeconds']:.1f}s", flush=True)
        estimate = engine.predict
    else:
        estimate = lambda values: predict(model, values, device)
    unrelated = unrelated_cameras(names)
    x = torch.from_numpy(images).float().to(device) / 255
    y = torch.from_numpy(targets).to(device)
    s = torch.from_numpy(np.zeros(targets.shape, bool) if soft is None else soft).to(device)
    print(f"TRAIN_STAGE index={stage} total=2 name={'check' if stage == 1 else 'final'}", flush=True)
    for epoch in range(epochs):
        print(f"TRAIN_EPOCH current={epoch+1} total={epochs}", flush=True)
        model.train()
        if mouth:
            frozen.eval()
            for _ in range(MOUTH_STEPS_PER_EPOCH):
                ids = torch.randint(0, len(x), (32,), device=device)
                if engine is not None:
                    engine.step(nuisance(x[ids]), y[ids], expression_loss, s[ids])
                    continue
                loss = expression_loss(model(nuisance(x[ids]))[0], y[ids], names, s[ids])
                optimizer.zero_grad(); loss.backward(); optimizer.step()
        else:
            for ids in torch.randperm(len(x), device=device).tensor_split(max(1, (len(x)+31)//32)):
                batch, labels = mix_cheeks(x[ids], y[ids], names)
                predicted, _ = model(augment(batch, unrelated))
                loss = expression_loss(predicted, labels, names)
                optimizer.zero_grad(); loss.backward(); optimizer.step()
            schedule.step()
        if (epoch+1) % 10 == 0 or epoch+1 == epochs:
            mse = float(np.nanmean((estimate(images)-targets)**2))
            print(f"Epoch {epoch+1}/{epochs}, training MSE={mse:.4f}", flush=True)
    if engine is not None:
        engine.sync()
        engine.close()
    return model


def develop(images, targets, names, epochs, device, stage, architecture=FIVE_CAMERA, encoder=None):
    """Fixed-epoch training. Epoch selection on one noisy round picked 1-6 epochs and undertrained."""
    if architecture != SIDES:
        return fit(images, targets, names, epochs, device, stage, architecture, encoder)
    rows, own, soft, shapes = side_rows(images, targets, names)
    shared = fit(rows, own, shapes, epochs, device, stage, MOUTH, encoder, soft)
    model = MouthSidesModel(len(shapes)).to(next(shared.parameters()).device)
    model.load_state_dict(shared.state_dict())
    return model


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", nargs="+", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--epochs", type=int, help="default 60, or 40 for the pretrained mouth model")
    parser.add_argument("--encoder", type=Path, default=ENCODER,
                        help="pretrained mouth encoder; without it every group uses the five-camera model")
    parser.add_argument("--never-puff", action="store_true",
                        help="also train on the completed tongue and other-group calibrations beside the capture as CheekPuff 0")
    args = parser.parse_args()
    architecture = architecture_for(labelled_names(args.captures), args.encoder)
    args.epochs = args.epochs or (60 if architecture == FIVE_CAMERA else MOUTH_EPOCHS)
    if args.epochs < 1 or args.output.exists():
        parser.error("epochs must be positive; output must be a new file")
    print(f"TRAIN_ARCHITECTURE {architecture}", flush=True)
    images, targets, groups, names = load_samples(args.captures, IMAGE_SIZE[architecture])
    rounds = [groups == r for r in range(3)]
    if min(r.sum() for r in rounds) < 10:
        parser.error("Capture both training rounds and the separate final holdout round")
    keep = [i for i in range(len(names)) if all((targets[r, i] == 0).sum() >= 3 and (targets[r, i] == 1).sum() >= 3
                                                  for r in rounds)]
    if architecture == SIDES:
        kept = {names[i] for i in keep}
        keep = [i for i in keep if {names[i].removesuffix("Left").removesuffix("Right") + side
                                    for side in ("Left", "Right")} <= kept]
    for i in sorted(set(range(len(names))) - set(keep)):
        print(f"TRAIN_SKIPPED {names[i]}", flush=True)
    if not keep:
        parser.error("Every pose was skipped, so there's nothing to calibrate.")
    targets, names = targets[:, keep], [names[i] for i in keep]
    puff = [i for i, name in enumerate(names) if name.startswith("CheekPuff")]
    never_puff = 0
    if args.never_puff and architecture == SIDES and puff:
        extra = load_never_puff(never_puff_captures(args.captures[0]), len(images), IMAGE_SIZE[architecture])
        row = np.full(len(names), np.nan, np.float32)
        row[puff] = 0
        images, never_puff = np.concatenate([images, extra]), len(extra)
        targets = np.concatenate([targets, np.tile(row, (never_puff, 1))])
        groups = np.concatenate([groups, np.full(never_puff, -1)])
        print(f"TRAIN_NEGATIVES stills={never_puff}", flush=True)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    train, holdout = groups != 2, groups == 2
    checked = develop(images[train], targets[train], names, args.epochs, device, 1, architecture, args.encoder)
    report = evaluate(predict(checked, images[holdout], device), targets[holdout], names)
    for name, metrics in report.items():
        print(f"HOLDOUT {name}: {json.dumps(metrics)}", flush=True)
    model = develop(images, targets, names, args.epochs, device, 2, architecture, args.encoder)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    provenance = {"pretrainedEncoder": {"sha256": hashlib.sha256(args.encoder.read_bytes()).hexdigest(),
                                        "data": "Ava-256 (CC BY-NC 4.0)"}} if architecture != FIVE_CAMERA else {}
    method = "per-side-halfway-ordinal-v1" if architecture == SIDES else "balanced-ordinal-fixed-v1"
    torch.save({"modelState": model.cpu().state_dict(), "expressionNames": names,
                "architecture": architecture, "imageSize": IMAGE_SIZE[architecture], **provenance,
                "schema": "extra-face-stills-v1", "validation": report,
                "epochs": args.epochs, "trainingMethod": method, "neverPuffStills": never_puff,
                "approvedForOutput": False}, args.output)
    args.output.with_suffix(".validation.json").write_text(json.dumps({
        "checkFrames": int(train.sum()), "holdoutFrames": int(holdout.sum()), "finalFrames": len(images),
        "epochs": args.epochs, "targets": report,
        "status": "Holdout scores come from a model trained on rounds 1-2; the saved model uses the same recipe on all rounds."
    }, indent=2))
    print(f"MODEL_READY path={args.output}", flush=True)


if __name__ == "__main__":
    main()
