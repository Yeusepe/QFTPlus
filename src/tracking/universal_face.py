"""Universal face model at runtime: one model for everyone, conditioned on the wearer's one-minute face setup.

Files (made by private/dev/universal/export.py): MODEL.area.onnx -- the network, raw 400x2000 five-camera strip in, mouth
embedding, tongue head and brow embedding out -- and MODEL.npz -- two numpy heads (mouth: missing anchors + 3-layer MLP;
brows: missing neutral + 2-layer MLP) and metadata pinned to the graph's sha256. Runs on ONNX Runtime (DirectML, CPU
fallback); no PyTorch. One pass over all five cameras: a shared front, then three tails -- mouth (cams 2/3,
anchor-conditioned outputs), tongue (cams 2/3, direction head) and brows (eye cams 0/1 + glabella cam 4, per side, against
the wearer's neutral). The enrollment file's anchor frames are encoded once at load. Per frame: network -> heads (numpy) ->
face_events -> the VRCFT module (extra expressions and tongue, both on UDP 27275). Tongue visibility stays on Meta's TongueOut.
"""
import hashlib
import json
import math
import os
import socket
from pathlib import Path
import numpy as np
from dataclasses import replace
from face_events import BROW, PUFF, SUCK, TONGUE, FaceEvents
from gpu_lock import GPU_LOCK
from capture_format import scan_frames
from label_capture import FRESH_NS, fresh_values

SCHEMA = "universal-face-v2"
META = {"schema", "names", "slots", "browNames", "imageSize", "approvedForOutput", "allowsNoEnrollment", "provenance", "graphSha256"}
HEAD = {"missing", "w1", "b1", "w2", "b2", "w3", "b3"}
BROW_HEAD = {"missing", "w1", "b1", "w2", "b2"}
SLOTS = ["neutral", "jaw_open", "pucker", "puff", "tongue_out", "suck"]
SIDES = ["puff_left", "puff_right"]
CHEEKS = ["CheekPuffLeft", "CheekPuffRight", "CheekSuckLeft", "CheekSuckRight"]
BROWS = [b + s for b in ("BrowInnerUp", "BrowOuterUp", "BrowLowerer", "BrowPinch") for s in ("Left", "Right")]
FAMILIES = {"puff": CHEEKS, "brows": BROWS}
NATIVE_PUFF = ("CheekPuffL", "CheekPuffR")
NATIVE_RAISE = {"BrowInnerUp": ("InnerBrowRaiserL", "InnerBrowRaiserR"), "BrowOuterUp": ("OuterBrowRaiserL", "OuterBrowRaiserR")}
SHARE_SECONDS = .15
RAISES = [b + s for b in NATIVE_RAISE for s in ("Left", "Right")]


def session(graph, cpu=False):
    """ONNX Runtime session: DirectML unless QFT_INFERENCE=cpu (or no DirectML), CPU as the fallback provider."""
    import onnxruntime as ort
    options = ort.SessionOptions()
    options.enable_mem_pattern = False
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.intra_op_num_threads = options.inter_op_num_threads = 1
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    providers = ["CPUExecutionProvider"]
    if not cpu and os.environ.get("QFT_INFERENCE", "auto") in ("auto", "directml") and "DmlExecutionProvider" in ort.get_available_providers():
        providers.insert(0, ("DmlExecutionProvider", {"device_id": max(0, min(15, int(os.environ.get("QFT_GPU_INDEX", "0")))),
                                                      "disable_metacommands": "true"}))
    with GPU_LOCK:
        return ort.InferenceSession(graph, sess_options=options, providers=providers)


def silu(x):
    return x / (1.0 + np.exp(-np.clip(x, -60.0, 60.0)))


def sigmoid(x):
    return 1.0 / (1.0 + np.exp(-np.clip(x, -60.0, 60.0)))


def head_forward(h, q, anchors, present):
    """q (512,), anchors (6, 512), present (6,) -> probabilities (outputs,)"""
    a = np.where(present[:, None] > 0, anchors, h["missing"])
    z = np.concatenate([q, a.ravel(), q - a[0], present])
    z = silu(h["w1"] @ z + h["b1"]); z = silu(h["w2"] @ z + h["b2"])
    return sigmoid(h["w3"] @ z + h["b3"])


def thumbnails(strip):
    """400x2000 strip -> the lower-face cameras (strip cameras 2, 3) as 16x16 means of 25x25-pixel blocks, 0..1 (2, 256).
    A puffed cheek brightens in its own camera. Same as tracking_model.cpp."""
    s = np.asarray(strip)[:, 800:1600].reshape(16, 25, 2, 16, 25).sum((1, 4), dtype=np.int64)
    return (s.transpose(1, 0, 2).reshape(2, 256) / 159375.).astype(np.float32)


def puff_camera(holds):
    """{slot: thumbnails (k, 2, 256)} of the face setup -> (rest (2, 256), per-side direction (2, 256), camera (2,),
    on-level (2,)), or None without the rest and one-cheek holds. Per side: the camera that moves most for its own hold, the
    direction from rest to its own and the both-cheek holds (1 at the hold), and an on-level above what every hold without
    that side gives. Same as calibrate.cpp."""
    if not {"neutral", *SIDES} <= holds.keys():
        return None
    rest = holds["neutral"].mean(0)
    moved = {s: np.abs(holds[s].mean(0) - rest).sum(1) for s in SIDES}
    camera, direction, level = [], [], []
    for side, (own, other) in enumerate((SIDES, SIDES[::-1])):
        c = int(np.argmax(moved[own] - moved[other]))
        d = np.concatenate([holds[own]] + ([holds["puff"]] if "puff" in holds else []))[:, c].mean(0) - rest[c]
        d = d / max(float(d @ d), 1e-9)
        off = np.concatenate([np.clip((holds[s][:, c] - rest[c]) @ d, 0., 1.) for s in holds if s not in ("puff", own)])
        camera.append(c); direction.append(d); level.append(float(np.clip(np.percentile(off, 99) + .1, .15, .5)))
    return rest, np.stack(direction).astype(np.float32), np.array(camera, np.float32), np.array(level, np.float32)


TONGUE_DIRECTION = {"tongue_out": (0., 0.), "tongue_up": (0., 1.), "tongue_down": (0., -1.), "tongue_left": (-1., 0.), "tongue_right": (1., 0.)}
TONGUE_HOLDS = [s for s in TONGUE_DIRECTION if s != "tongue_out"]
RIDGE, TONGUE_REACH, TONGUE_GAIN = 8., .15, 2.
TONGUE_HISTORY = 4
TONGUE_TARGET = {"Out": (0., 0.), "Left": (-1., 0.), "Right": (1., 0.), "Up": (0., 1.), "Down": (0., -1.),
                 "UpLeft": (-.7071, .7071), "UpRight": (.7071, .7071)}
TONGUE_BENCHMARKS, BENCHMARK_FRAMES, TRIM = 4, 300, 3


def tongue_map(holds, extra=None):
    """{slot: mouth embeddings (k, 512)} of the held tongue poses, optional extra (embeddings (n, 512), directions (n, 2))
    from benchmarks -> (mean, scale, weights (512, 2), gains (left, right, down, up)), or None unless all five poses are
    there. The holds weigh as much as the extra frames together; the gains come from the holds."""
    if not set(TONGUE_DIRECTION) <= set(holds):
        return None
    x = np.concatenate([holds[s] for s in TONGUE_DIRECTION]).astype(np.float64)
    y = np.concatenate([np.tile(TONGUE_DIRECTION[s], (len(holds[s]), 1)) for s in TONGUE_DIRECTION])
    if extra is not None and len(extra[0]):
        r = max(1, round(len(extra[0]) / len(x)))
        x, y = np.concatenate([np.tile(x, (r, 1)), extra[0]]), np.concatenate([np.tile(y, (r, 1)), extra[1]])
    mean, scale = x.mean(0), x.std(0) + 1e-3
    xn = (x - mean) / scale
    weights = xn.T @ np.linalg.solve(xn @ xn.T + RIDGE * x.shape[1] * np.eye(len(x)), y)
    held = {s: np.median(((holds[s] - mean) / scale) @ weights, 0) for s in TONGUE_DIRECTION}
    gain = lambda reach: min(1. / reach, TONGUE_GAIN) if reach >= TONGUE_REACH else 1.
    gains = (gain(-held["tongue_left"][0]), gain(held["tongue_right"][0]), gain(-held["tongue_down"][1]), gain(held["tongue_up"][1]))
    return mean.astype(np.float32), scale.astype(np.float32), weights.astype(np.float32), gains


def tongue_direction(q, mapping):
    """Mouth embedding (512,) -> (horizontal, vertical) in -1..1, + = the wearer's right, up."""
    mean, scale, weights, (left, right, down, up) = mapping
    h, v = ((q - mean) / scale) @ weights
    return float(np.clip(h * (right if h > 0 else left), -1., 1.)), float(np.clip(v * (up if v > 0 else down), -1., 1.))


def brow_forward(h, w, neutral, present):
    """w (480,), neutral (480,), present 0/1 -> per-side brows (8,)"""
    n = neutral if present else h["missing"]
    return sigmoid(h["w2"] @ silu(h["w1"] @ np.concatenate([w, w - n, np.float32([present])]) + h["b1"]) + h["b2"])


class UniversalFace:
    def __init__(self, *args, **kwargs):
        try:
            self._load(*args, **kwargs)
        except BaseException:
            with GPU_LOCK:
                self.model = None
            raise

    def _load(self, path, enrollment=None, enabled=False, *, families=tuple(FAMILIES), bias=0., log=None, tongue=None,
              history=(), benchmarks=()):
        """path: MODEL.npz (MODEL.area.onnx next to it); history: earlier face setup files, newest first; benchmarks: recorded
        benchmark prefixes (captures/benchmark-...), newest first."""
        path = Path(path).resolve()
        graph_path = path.with_suffix(".area.onnx")
        with np.load(path, allow_pickle=False) as z:
            meta = json.loads(str(z["meta"]))
            head = {k[5:]: z[k] for k in z.files if k.startswith("head_")}
            brow_head = {k[5:]: z[k] for k in z.files if k.startswith("brow_")}
        graph = graph_path.read_bytes()
        if (meta.get("schema") != SCHEMA or set(meta) != META or meta["imageSize"] not in (64, 96, 128) or set(head) != HEAD
                or set(brow_head) != BROW_HEAD or meta["slots"] != SLOTS or meta["browNames"] != BROWS
                or hashlib.sha256(graph).hexdigest() != meta["graphSha256"]):
            raise ValueError("Not a universal face model: " + path.name)
        if (enabled or (tongue is not None and tongue.enabled)) and meta["approvedForOutput"] is not True:
            raise ValueError("Universal face model has not been approved for output: " + path.name)
        self.names = list(meta["names"])
        if not set(CHEEKS) <= set(self.names):
            raise ValueError("Universal face model lacks required outputs: " + path.name)
        self.head = {k: v.astype(np.float32) for k, v in head.items()}
        self.brow_head = {k: v.astype(np.float32) for k, v in brow_head.items()}
        self.graph, self.graph_sha = graph, meta["graphSha256"]
        self.model = session(graph)
        try:
            self.run(np.zeros((400, 2000), np.uint8))
        except Exception as error:
            print("INFERENCE_FALLBACK cpu: " + str(error), flush=True)
            cpu = session(graph, cpu=True)
            with GPU_LOCK:
                self.model = cpu
        print("INFERENCE_BACKEND " + self.model.get_providers()[0] + " " + str(graph_path), flush=True)
        self.anchors, self.present, sides, self.brow_neutral, brow_rest = self.encode_enrollment(enrollment, history, benchmarks)
        if not self.present.any() and meta["allowsNoEnrollment"] is not True:
            raise ValueError("Run the one-minute face setup first: this model needs it.")
        reach, cheek_rest = {}, {}
        if self.present[0]:
            level = lambda slot, cheeks: float(head_forward(self.head, self.anchors[slot], self.anchors, self.present)[
                [self.names.index(c) for c in cheeks]].max())
            cheek_rest = {**dict.fromkeys(CHEEKS[:2], level(0, CHEEKS[:2])), **dict.fromkeys(CHEEKS[2:], level(0, CHEEKS[2:]))}
            for slot, cheeks in ((3, CHEEKS[:2]), (5, CHEEKS[2:])):
                if self.present[slot] and (full := level(slot, cheeks)) - cheek_rest[cheeks[0]] >= .2:
                    reach.update(dict.fromkeys(cheeks, full))
            if not self.present[3] and sides:
                full = max(float(head_forward(self.head, q, self.anchors, self.present)[[self.names.index(c) for c in CHEEKS[:2]]].max())
                           for q in sides.values())
                if full - cheek_rest[CHEEKS[0]] >= .2:
                    reach.update(dict.fromkeys(CHEEKS[:2], full))
        self.puff_camera = puff_camera(self.puff_holds)
        if self.puff_camera is not None:
            self.puff_amount = (cheek_rest[CHEEKS[0]], reach.get(CHEEKS[0], 1.))
            cheek_rest.update(dict.fromkeys(CHEEKS[:2], 0.)); reach.update(dict.fromkeys(CHEEKS[:2], 1.))
            self.puff_reference, self.puff_ns = self.puff_camera[0].copy(), None
        brow_rest = {k: v for k, v in brow_rest.items() if not k.startswith(tuple(NATIVE_RAISE))}
        puff = PUFF if self.puff_camera is None else replace(PUFF, rise=True)
        self.events = FaceEvents({"CheekPuffLeft": puff, "CheekPuffRight": puff, "CheekSuckLeft": SUCK, "CheekSuckRight": SUCK,
                                  "TongueOut": TONGUE, **dict.fromkeys(BROWS, BROW)}, neutral={**brow_rest, **cheek_rest}, reach=reach,
                                 bias=bias, log=log)
        self.sent = [n for f in families if f in FAMILIES for n in FAMILIES[f] if n not in RAISES]
        self.send_shares = "brows" in families
        self.share, self.share_ns = {}, None
        self.enabled, self.tongue = enabled, tongue
        self.socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    def run(self, strip):
        """400x2000 uint8 strip -> mouth embedding (512,), tongue head (7,), brow embedding (480,)"""
        with GPU_LOCK:
            try:
                q, t, w = self.model.run(None, {"cameras": np.ascontiguousarray(strip)})
            except Exception as error:
                if self.model.get_providers()[0] == "CPUExecutionProvider":
                    raise
                print("INFERENCE_FALLBACK cpu: " + str(error), flush=True)
                self.model = session(self.graph, cpu=True)
                q, t, w = self.model.run(None, {"cameras": np.ascontiguousarray(strip)})
        return q[0], t[0], w[0]

    def encode_enrollment(self, enrollment, history=(), benchmarks=()):
        """Face setup file -> mouth anchors (6, 512) + presence (6,), the one-cheek puff embeddings {side: (512,)}, brow
        neutral embedding (480,) + presence, and the brows' resting outputs (face_events' starting neutral); sets the
        wearer's tongue direction mapping from the held tongue poses, pooled with complete earlier setups' (the newest counts
        double: its session decides the offset, the earlier ones add how this person varies) and with recent benchmarks'
        tongue prompts. Runs once, at load."""
        self.tongue_map, self.puff_holds, holds = None, {}, {}
        anchors, present = np.zeros((6, 512), np.float32), np.zeros(6, np.float32)
        sides, brow_neutral, brow_present, rest = {}, np.zeros(480, np.float32), 0., {}
        if not enrollment or not Path(enrollment).is_file():
            return anchors, present, sides, (brow_neutral, brow_present), rest
        with np.load(enrollment, allow_pickle=False) as z:
            if json.loads(str(z["meta"]))["schema"] != "face-enrollment-v1":
                return anchors, present, sides, (brow_neutral, brow_present), rest
            for slot in SLOTS + SIDES + TONGUE_HOLDS:
                if f"slot_{slot}" in z:
                    q, t, w = (np.stack(x) for x in zip(*(self.run(f) for f in z[f"slot_{slot}"])))
                    self.puff_holds[slot] = np.stack([thumbnails(f) for f in z[f"slot_{slot}"]])
                    if slot in TONGUE_DIRECTION:
                        holds[slot] = q
                        if slot in TONGUE_HOLDS:
                            continue
                    if slot in SIDES:
                        sides[slot] = q.mean(0)
                        continue
                    anchors[SLOTS.index(slot)], present[SLOTS.index(slot)] = q.mean(0), 1.0
                    if slot == "neutral":
                        brow_neutral, brow_present = w.mean(0), 1.
                        rest = dict(zip(BROWS, np.median([brow_forward(self.brow_head, r, brow_neutral, 1.) for r in w], 0).tolist()))
        if set(TONGUE_DIRECTION) <= set(holds):
            earlier = [h for h in map(self.tongue_holds, list(history)[:TONGUE_HISTORY]) if set(TONGUE_DIRECTION) <= set(h)]
            holds = {s: np.concatenate([holds[s], holds[s], *(h[s] for h in earlier)]) for s in TONGUE_DIRECTION}
        extra = [e for e in map(self.benchmark_tongue, list(benchmarks)[:TONGUE_BENCHMARKS]) if len(e[0])]
        self.tongue_map = tongue_map(holds, (np.concatenate([e[0] for e in extra]), np.concatenate([e[1] for e in extra])) if extra else None)
        return anchors, present, sides, (brow_neutral, brow_present), rest

    def tongue_holds(self, enrollment):
        """An earlier face setup file -> {tongue hold: mouth embeddings (k, 512)}; {} if it is gone or not a setup."""
        try:
            with np.load(enrollment, allow_pickle=False) as z:
                if json.loads(str(z["meta"]))["schema"] != "face-enrollment-v1":
                    return {}
                return {s: np.stack([self.run(f)[0] for f in z[f"slot_{s}"]]) for s in TONGUE_DIRECTION
                        if f"slot_{s}" in z and z[f"slot_{s}"].shape[1:] == (400, 2000)}
        except (OSError, ValueError, KeyError):
            return {}

    def benchmark_tongue(self, prefix):
        """A recorded benchmark -> mouth embeddings (k, 512) and prompted directions (k, 2) of its tongue prompts' frames where
        Meta's TongueOut is above .5. Computed once per model and cached next to the recording; empty if unreadable."""
        prefix, empty = Path(prefix), (np.zeros((0, 512), np.float32), np.zeros((0, 2)))
        cache = prefix.with_name(prefix.name + ".tongue.npz")
        try:
            with np.load(cache, allow_pickle=False) as z:
                if str(z["model"]) == self.graph_sha:
                    return z["q"], z["y"]
        except (OSError, KeyError, ValueError):
            pass
        try:
            steps = json.loads(prefix.with_name(prefix.name + ".qpsession.json").read_text(encoding="utf-8"))["steps"]
            names, nt, out = None, [], []
            for line in prefix.with_name(prefix.name + ".qplabel.jsonl").open(encoding="utf-8"):
                r = json.loads(line)
                if r.get("type") == "schema":
                    names = r["names"]
                elif r.get("type") == "sample" and names and "TongueOut" in names:
                    nt.append(int(r["arrivalMonotonicNs"])); out.append(float(r["values"][names.index("TongueOut")]))
            capture = prefix.with_name(prefix.name + ".qpcap")
            entries = scan_frames(capture, 0x1F)
        except (OSError, ValueError, KeyError, TypeError):
            return empty
        if not nt:
            return empty
        t, nt, out = np.array([e[1] for e in entries], np.int64), np.array(nt, np.int64), np.array(out)
        i = np.clip(np.searchsorted(nt, t, side="right") - 1, 0, None)
        visible = (t >= nt[i]) & (t - nt[i] <= FRESH_NS) & (out[i] > .5)
        visible &= np.array([(e[2], e[3]) == (2000, 400) for e in entries])
        picks, y = [], []
        for s in steps:
            k = s.get("condition", "").removeprefix("target:Tongue")
            if s.get("startNs") is None or not s["condition"].startswith("target:Tongue") or k not in TONGUE_TARGET:
                continue
            idx = np.flatnonzero((t >= s["startNs"]) & (t < s["endNs"]) & visible)[TRIM:-TRIM]
            picks += idx.tolist(); y += [TONGUE_TARGET[k]] * len(idx)
        if not picks:
            return empty
        keep = np.linspace(0, len(picks) - 1, min(len(picks), BENCHMARK_FRAMES)).astype(int)
        picks, y = np.array(picks)[keep], np.array(y)[keep]
        q = []
        with capture.open("rb") as f:
            for j in picks:
                offset, _, width, height = entries[j]
                f.seek(offset)
                q.append(self.run(np.frombuffer(f.read(width * height), np.uint8).reshape(height, width))[0])
        q = np.stack(q)
        try:
            with cache.with_suffix(".tmp").open("wb") as f:
                np.savez(f, q=q, y=y, model=np.array(self.graph_sha))
            cache.with_suffix(".tmp").replace(cache)
        except OSError:
            pass
        return q, y

    def puff_sides(self, thumbs, top, now_ns):
        """Each side from its own camera against a rest image learned on quiet frames; the overall puff amount (head and Meta,
        `top`, on its calibrated rest/reach) only fades it in as a veto. The calibrated on-level maps to .5 (face_events).
        Same as tracking_model.cpp."""
        rest, direction, camera, level = self.puff_camera
        n, reach = self.puff_amount
        amount = min(1., max(0., (top - n) * min(2.5, max(.7, 1. / max(reach - n, 1e-3)))))
        gate = min(1., max(0., (amount - .03) / .07))
        side = [min(1., max(0., float((thumbs[int(c)] - self.puff_reference[int(c)]) @ d))) for c, d in zip(camera, direction)]
        values = [v * .5 / t if v < t else .5 + (v - t) * .5 / (1 - t) for v, t in zip((s * gate for s in side), map(float, level))]
        dt = 0. if self.puff_ns is None else min(.1, max(0., (now_ns - self.puff_ns) / 1e9))
        self.puff_ns = now_ns
        if amount < .05 and max(side) < .15:
            self.puff_reference += np.float32(.03 * dt) * np.sign(thumbs - self.puff_reference)
        return values

    def update(self, strip, native=None, native_names=(), now_ns=0):
        q, t, w = self.run(strip)
        p = dict(zip(self.names, head_forward(self.head, q, self.anchors, self.present)))
        p.update(zip(BROWS, brow_forward(self.brow_head, w, *self.brow_neutral)))
        horizontal, vertical = tongue_direction(q, self.tongue_map) if self.tongue_map else np.tanh(t[1:3])
        fresh = fresh_values(native, native_names, now_ns)
        nv = fresh or {}
        native_puff = [nv.get(n, 0.) for n in NATIVE_PUFF]
        if self.puff_camera is not None:
            p["CheekPuffLeft"], p["CheekPuffRight"] = self.puff_sides(
                thumbnails(strip), max(p["CheekPuffLeft"], p["CheekPuffRight"], *native_puff), now_ns)
        else:
            p["CheekPuffLeft"], p["CheekPuffRight"] = max(p["CheekPuffLeft"], native_puff[0]), max(p["CheekPuffRight"], native_puff[1])
        dt = 0. if self.share_ns is None else max(0., (now_ns - self.share_ns) / 1e9)
        self.share_ns = now_ns
        for base, (left, right) in NATIVE_RAISE.items():
            a, b = p[base + "Left"] + .05, p[base + "Right"] + .05
            k = 1. if base not in self.share else 1. - math.exp(-dt / SHARE_SECONDS)
            self.share[base] = share = self.share.get(base, 0.) + k * (a / (a + b) - self.share.get(base, 0.))
            if left in nv and right in nv:
                amp = (nv[left] + nv[right]) / 2
                p[base + "Left"], p[base + "Right"] = min(1., amp * 2 * share), min(1., amp * 2 * (1 - share))
        tongue_native = nv.get("TongueOut", 0.)
        values = {n: float(p[n]) for n in CHEEKS + BROWS}
        values["TongueOut"] = tongue_native
        out = self.events.step(values, native, fresh, now_ns)
        if self.enabled:
            packet = {"version": 1, "enabled": True, "values": {n: out[n] for n in self.sent}}
            if self.send_shares:
                packet["shares"] = {b + side: round(float(v), 4) for b, sh in self.share.items() for side, v in (("Left", sh), ("Right", 1 - sh))}
            self.socket.sendto(json.dumps(packet).encode(), ("127.0.0.1", 27275))
        if self.tongue is not None:
            self.tongue.send(max(0.4, tongue_native), horizontal, vertical, self.events.on["TongueOut"])

    def close(self):
        with GPU_LOCK:
            self.model = None
        self.events.close()
        if self.tongue is not None:
            self.tongue.close()
        if self.enabled:
            self.socket.sendto(b'{"version":1,"enabled":false,"values":{}}', ("127.0.0.1", 27275))
        self.socket.close()
