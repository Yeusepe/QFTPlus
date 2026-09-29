from __future__ import annotations
import json
import os
import queue
import shutil
import subprocess
import tempfile
import threading
import time
from pathlib import Path
import numpy as np
import torch
from torch import nn

ROOT = Path(__file__).resolve().parent
EXE = Path(os.environ.get("QFT_TRAINER") or ROOT / ("qft-trainer.exe" if os.name == "nt" else "qft-trainer"))
CACHE = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / ".cache"))) / "QFT-Plus" / "trainer"


class Unavailable(RuntimeError):
    pass


def enabled() -> bool:
    return os.environ.get("QFT_GPU_TRAINING", "1") != "0" and EXE.exists()


def conv_bn_pairs(model: nn.Module):
    for name, module in model.named_modules():
        if isinstance(module, nn.Sequential):
            children = list(module.named_children())
            for (a, conv), (_, bn) in zip(children, children[1:]):
                if isinstance(conv, nn.Conv2d) and isinstance(bn, nn.BatchNorm2d):
                    yield (f"{name}.{a}" if name else a), conv, bn


def fold_batchnorm(model: nn.Module) -> nn.Module:
    with torch.no_grad():
        for _, conv, bn in conv_bn_pairs(model):
            weight, bias = nn.utils.fusion.fuse_conv_bn_weights(
                conv.weight, None, bn.running_mean, bn.running_var, bn.eps, bn.weight, bn.bias)
            conv.weight.copy_(weight)
            bn.bias.copy_(bias)
            bn.weight.fill_(1.0)
            bn.running_mean.zero_()
            bn.running_var.fill_(1.0 - bn.eps)
            bn.weight.requires_grad_(False)
    return model


def _tensors(model: nn.Module) -> dict[str, torch.Tensor]:
    values = {}
    for path, conv, bn in conv_bn_pairs(model):
        values[path + ".w"] = conv.weight
        values[path + ".b"] = bn.bias
    for path, module in model.named_modules():
        if isinstance(module, nn.Linear):
            values[path + ".w"] = module.weight
            values[path + ".b"] = module.bias
    return values


def _linear(name, model):
    return name.endswith(".w") and isinstance(model.get_submodule(name[:-2]), nn.Linear)


def export(model: nn.Module, folder: Path) -> None:
    folder.mkdir(parents=True, exist_ok=True)
    shapes = {}
    for name, value in _tensors(model).items():
        array = value.detach().float().cpu()
        array = (array.T if _linear(name, model) else array).contiguous().numpy()
        array.astype("<f4").tofile(folder / f"{name}.bin")
        shapes[name] = list(array.shape)
    (folder / "manifest.json").write_text(json.dumps(shapes))


def load(model: nn.Module, folder: Path) -> nn.Module:
    with torch.no_grad():
        for name, target in _tensors(model).items():
            value = torch.from_numpy(np.fromfile(folder / f"{name}.bin", "<f4"))
            value = value.reshape(target.shape[::-1]).T if _linear(name, model) else value.reshape(target.shape)
            target.copy_(value)
    return model


class Trainer:

    def __init__(self, network: nn.Module, **spec):
        self.folder = Path(tempfile.mkdtemp(prefix="qft-gpu-"))
        export(network, self.folder / "params")
        CACHE.mkdir(parents=True, exist_ok=True)
        self.log = open(self.folder / "trainer.log", "w", encoding="utf-8", errors="replace")
        environment = dict(os.environ)
        if spec.pop("safe", False):
            environment["MEGANEURA_DISABLE_COOP"] = "1"
        try:
            self.process = subprocess.Popen([str(EXE)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log,
                                            text=True, env=environment, bufsize=1,
                                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        except OSError as error:
            raise Unavailable(f"trainer did not start: {error}") from error
        self.replies: queue.Queue = queue.Queue()
        threading.Thread(target=self._read, daemon=True).start()
        self.init = self.call({"cmd": "init", "params": str(self.folder / "params"), "cache": str(CACHE), **spec}, timeout=180)

    def _read(self):
        for line in self.process.stdout:
            self.replies.put(line)
        self.replies.put(None)

    def call(self, command: dict, timeout: float = 60) -> dict:
        try:
            self.process.stdin.write(json.dumps(command) + "\n")
            self.process.stdin.flush()
            line = self.replies.get(timeout=timeout)
        except (OSError, queue.Empty) as error:
            raise Unavailable(f"trainer stopped responding ({command['cmd']})") from error
        if line is None:
            self.log.flush()
            tail = (self.folder / "trainer.log").read_text(errors="replace").strip().splitlines()[-1:] or ["no output"]
            raise Unavailable(f"trainer exited: {tail[0][:200]}")
        reply = json.loads(line)
        if "error" in reply:
            raise Unavailable(reply["error"])
        return reply

    def _write(self, folder: Path, **arrays) -> Path:
        folder.mkdir(parents=True, exist_ok=True)
        for name, value in arrays.items():
            np.ascontiguousarray(value, dtype="<f4").tofile(folder / f"{name}.bin")
        return folder

    def step(self, **arrays) -> tuple[float, float]:
        reply = self.call({"cmd": "step", "dir": str(self._write(self.folder / "step", **arrays))})
        return float(reply["loss"]), float(reply["seconds"])

    def predict(self, images: np.ndarray) -> np.ndarray:
        path = self._write(self.folder / "predict", x=images) / "x.bin"
        out = self.folder / "predict" / "out.bin"
        count = self.call({"cmd": "predict", "x": str(path), "out": str(out)}, timeout=120)["count"]
        return np.fromfile(out, "<f4").reshape(count, -1)

    def pull(self, model: nn.Module) -> nn.Module:
        self.call({"cmd": "save", "dir": str(self.folder / "pulled")})
        return load(model, self.folder / "pulled")

    def gradients(self) -> dict[str, np.ndarray]:
        written = self.call({"cmd": "grads", "dir": str(self.folder / "grads")})["written"]
        return {name: np.fromfile(self.folder / "grads" / f"{name}.bin", "<f4") for name in written}

    def close(self):
        try:
            self.call({"cmd": "quit"}, timeout=5)
        except Unavailable:
            pass
        try:
            self.process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.process.kill()
        self.log.close()
        shutil.rmtree(self.folder, ignore_errors=True)


def start(network: nn.Module, **spec) -> Trainer:
    try:
        return Trainer(network, **spec)
    except Unavailable as first:
        try:
            return Trainer(network, safe=True, **spec)
        except Unavailable:
            raise first from None


def verify(trainer: Trainer, reference: nn.Module, loss_of, arrays: dict) -> float:
    started = time.perf_counter()
    reference.zero_grad(set_to_none=True)
    loss_of(reference).backward()
    cpu_seconds = time.perf_counter() - started
    trainer.step(**arrays)
    got = trainer.gradients()
    expected = {name: tensor.grad for name, tensor in _tensors(reference).items()
                if tensor.requires_grad and tensor.grad is not None}
    if set(expected) - set(got):
        raise Unavailable("GPU trainer is missing gradients for " + ", ".join(sorted(set(expected) - set(got))[:3]))
    for name, grad in expected.items():
        want = (grad.T if _linear(name, reference) else grad).contiguous().flatten().double()
        have = torch.from_numpy(got[name]).double()
        scale = float(want.norm())
        if scale == 0:
            continue
        cosine = float(torch.dot(want, have) / (scale * float(have.norm()) + 1e-30))
        relative = float((have - want).norm()) / scale
        if cosine < 0.99 or relative > 0.15:
            raise Unavailable(f"GPU gradient check failed at {name} (cosine {cosine:.3f})")
    return cpu_seconds


class Pace:

    def __init__(self):
        self.cpu, self.gpu = None, []

    def record(self, seconds: float) -> None:
        if self.cpu is None or len(self.gpu) >= 2:
            return
        self.gpu.append(seconds)
        if len(self.gpu) == 2 and min(self.gpu) >= self.cpu:
            raise Unavailable(f"GPU step {min(self.gpu):.2f} s is not faster than CPU {self.cpu:.2f} s")



TONGUE_COLUMN = np.array([0.0, 1.4, 2.2, 2.2, 2.5, 2.5, 2.5, 2.2, 2.2, 2.0], np.float32)
TONGUE_BOOST = np.array([0.0, 2.0, 6.0, 6.0, 4.0, 4.0, 4.0, 4.0, 4.0, 2.0], np.float32)


def tongue_inputs(images: np.ndarray, target: np.ndarray, names: list[str]) -> dict:
    vi = names.index("visibility")
    column = TONGUE_COLUMN if len(names) == len(TONGUE_COLUMN) else np.where(np.arange(len(names)) == vi, 0.0, 1.0)
    boost = TONGUE_BOOST if len(names) == len(TONGUE_BOOST) else np.where(np.arange(len(names)) == vi, 0.0, 6.0)
    known = np.isfinite(target)
    y = np.nan_to_num(target)
    visible = (y[:, vi] >= .5).astype(np.float32)
    cv = np.zeros_like(y)
    cv[:, vi] = 1.8 * np.where(visible > 0, 1.25, 1.70) / len(y)
    w = known * visible[:, None] * column * (1 + boost * (np.abs(y) > .1))
    return {"x": images, "y": y, "cv": cv, "wn": w / max(1.0, float(w.sum()))}


class TongueEngine:
    def __init__(self, model, train_loader, validation_loader, names, learning_rate, loss_fn, summarize):
        self.model, self.names, self.loader, self.summarize = model, names, train_loader, summarize
        fold_batchnorm(model)
        images, targets, natives = zip(*((i.numpy(), t.numpy(), n.numpy()) for i, t, n in validation_loader))
        self.validation = np.concatenate(images), np.concatenate(targets), np.concatenate(natives)
        self.batch = train_loader.batch_size
        signed = [float(name in {"horizontal", "vertical", "twist"}) for name in names]
        self.trainer = start(model, model="tongue", batch=self.batch, eval_batch=self.batch, targets=len(names),
                             size=int(self.validation[0].shape[-1]), frozen_blocks=1, lr=learning_rate,
                             weight_decay=1e-4, signed=signed)
        self.loss_fn = loss_fn
        self.pace = Pace()

    def train_epoch(self) -> float:
        total, count = 0.0, 0
        for images, target, _native in self.loader:
            if len(images) != self.batch:
                continue
            arrays = tongue_inputs(images.numpy(), target.numpy(), self.names)
            if self.pace.cpu is None:
                import copy
                reference = copy.deepcopy(self.model).eval()
                self.pace.cpu = verify(self.trainer, reference,
                                       lambda m: self.loss_fn(m(images), target, self.names), arrays)
                with torch.no_grad():
                    loss = float(self.loss_fn(reference(images), target, self.names))
            else:
                loss, seconds = self.trainer.step(**arrays)
                self.pace.record(seconds)
            total += loss * len(images)
            count += len(images)
        return total / max(1, count)

    def evaluate(self) -> dict:
        images, target, native = self.validation
        return self.summarize(self.trainer.predict(images), target, native, self.names)

    def sync(self):
        self.trainer.pull(self.model)

    def close(self):
        self.trainer.close()



def expression_inputs(mouth: np.ndarray, target: np.ndarray, names: list[str], soft: np.ndarray | None = None) -> dict:
    """Matrix form of train_extra_face.expression_loss; soft targets only keep their ordering."""
    b, t = target.shape
    soft = np.zeros((b, t), bool) if soft is None else soft
    pairs_max, sides_max = t * b * (b - 1) // 2, max(1, b * t)
    wl = np.zeros((b, t), np.float32)
    pairs = np.zeros((pairs_max, b * t), np.float32)
    gaps, pair_weights = np.zeros((pairs_max, 1), np.float32), np.zeros((pairs_max, 1), np.float32)
    sides = np.zeros((sides_max, b * t), np.float32)
    side_targets, side_weights = np.zeros((sides_max, 1), np.float32), np.zeros((sides_max, 1), np.float32)
    row = 0
    for i in range(t):
        valid = np.flatnonzero(np.isfinite(target[:, i]))
        if not len(valid):
            continue
        values = target[valid, i]
        levels, inverse, counts = np.unique(values, return_inverse=True, return_counts=True)
        wl[valid, i] = np.where(soft[valid, i], 0.0, 1.0 / (len(levels) * counts[inverse]))
        a, c = np.meshgrid(valid, valid, indexing="ij")
        gap = target[a, i] - target[c, i]
        ordered = gap > 0
        for first, second, g in zip(a[ordered], c[ordered], gap[ordered]):
            pairs[row, first * t + i], pairs[row, second * t + i] = 1.0, -1.0
            gaps[row] = min(g, .1) if soft[first, i] or soft[second, i] else g
            pair_weights[row] = .25 / ordered.sum()
            row += 1
    row = 0
    for i, name in enumerate(names):
        opposite = name.removesuffix("Left") + "Right"
        if name.endswith("Left") and opposite in names:
            j = names.index(opposite)
            valid = np.flatnonzero(np.isfinite(target[:, i]) & np.isfinite(target[:, j]))
            for sample in valid:
                sides[row, sample * t + i], sides[row, sample * t + j] = 1.0, -1.0
                side_targets[row] = target[sample, i] - target[sample, j]
                side_weights[row] = .5 / len(valid)
                row += 1
    return {"x": mouth, "y": np.nan_to_num(target), "wl": wl, "pairs": pairs, "gaps": gaps,
            "pair_weights": pair_weights, "sides": sides, "side_targets": side_targets, "side_weights": side_weights}


class MouthEngine:
    def __init__(self, model, names, batch, frozen_blocks, learning_rate, weight_decay, size):
        self.model, self.names, self.batch = model, names, batch
        fold_batchnorm(model)
        self.trainer = start(model, model="mouth", batch=batch, eval_batch=batch, targets=len(names), size=size,
                             frozen_blocks=frozen_blocks, lr=learning_rate, weight_decay=weight_decay,
                             pairs=len(names) * batch * (batch - 1) // 2, sides=max(1, batch * len(names)))
        self.pace = Pace()

    def step(self, cameras: torch.Tensor, target: torch.Tensor, loss_fn, soft: torch.Tensor | None = None) -> float:
        arrays = expression_inputs(cameras[:, 2:4].numpy(), target.numpy(), self.names,
                                   None if soft is None else soft.numpy())
        if self.pace.cpu is None:
            import copy
            reference = copy.deepcopy(self.model).eval()
            self.pace.cpu = verify(self.trainer, reference, lambda m: loss_fn(m(cameras)[0], target, self.names, soft), arrays)
            with torch.no_grad():
                return float(loss_fn(reference(cameras)[0], target, self.names, soft))
        loss, seconds = self.trainer.step(**arrays)
        self.pace.record(seconds)
        return loss

    def predict(self, images: np.ndarray) -> np.ndarray:
        return self.trainer.predict(images[:, 2:4].astype(np.float32) / 255)

    def sync(self):
        self.trainer.pull(self.model)

    def close(self):
        self.trainer.close()
