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
from face_events import BROW, PUFF, SUCK, TONGUE, FaceEvents
from gpu_lock import GPU_LOCK
from label_capture import fresh_values

SCHEMA = "universal-face-v2"
META = {"schema", "names", "slots", "browNames", "imageSize", "approvedForOutput", "allowsNoEnrollment", "provenance", "graphSha256"}
HEAD = {"missing", "w1", "b1", "w2", "b2", "w3", "b3"}
BROW_HEAD = {"missing", "w1", "b1", "w2", "b2"}
SLOTS = ["neutral", "jaw_open", "pucker", "puff", "tongue_out", "suck"]
SIDES = ["puff_left", "puff_right"]
MAX_UNMIX_COND = 10.
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


def puff_depth(q, neutral, axis):
    """Per mouth camera, how far q has moved from neutral toward the both-cheeks puff (1 = all the way) -> (2,)"""
    return np.clip(((q.reshape(2, -1) - neutral) * axis).sum(1), 0., None)


TONGUE_DIRECTION = {"tongue_out": (0., 0.), "tongue_up": (0., 1.), "tongue_down": (0., -1.), "tongue_left": (-1., 0.), "tongue_right": (1., 0.)}
TONGUE_HOLDS = [s for s in TONGUE_DIRECTION if s != "tongue_out"]
RIDGE, TONGUE_REACH, TONGUE_GAIN = 2., .15, 2.


def tongue_map(holds):
    """{slot: mouth embeddings (k, 512)} of the held tongue poses -> (mean, scale, weights (512, 2), gains (left, right,
    down, up)), or None unless all five poses are there."""
    if not set(TONGUE_DIRECTION) <= set(holds):
        return None
    x = np.concatenate([holds[s] for s in TONGUE_DIRECTION]).astype(np.float64)
    y = np.concatenate([np.tile(TONGUE_DIRECTION[s], (len(holds[s]), 1)) for s in TONGUE_DIRECTION])
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
    def __init__(self, path, enrollment=None, enabled=False, *, families=tuple(FAMILIES), bias=0., log=None, tongue=None):
        """path: MODEL.npz (MODEL.area.onnx next to it)."""
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
        self.graph = graph
        self.model = session(graph)
        try:
            self.run(np.zeros((400, 2000), np.uint8))
        except Exception as error:
            print("INFERENCE_FALLBACK cpu: " + str(error), flush=True)
            self.model = session(graph, cpu=True)
        print("INFERENCE_BACKEND " + self.model.get_providers()[0] + " " + str(graph_path), flush=True)
        self.anchors, self.present, sides, self.brow_neutral, brow_rest = self.encode_enrollment(enrollment)
        if not self.present.any() and meta["allowsNoEnrollment"] is not True:
            raise ValueError("Run the one-minute face setup first: this model needs it.")
        self.puff_axis, reach = None, {}
        if self.present[0] and self.present[3]:
            n, d = self.anchors[0].reshape(2, -1), (self.anchors[3] - self.anchors[0]).reshape(2, -1)
            axis, unmix = d / np.maximum((d * d).sum(1, keepdims=True), 1e-6), None
            if len(sides) == 2:
                mix = np.stack([puff_depth(sides[s], n, axis) for s in SIDES], 1)
                unmix = np.linalg.inv(mix) if np.linalg.cond(mix) < MAX_UNMIX_COND else None
            self.puff_axis = (n, axis, unmix)
        cheek_rest = {}
        if self.present[0]:
            level = lambda slot, cheeks: float(head_forward(self.head, self.anchors[slot], self.anchors, self.present)[
                [self.names.index(c) for c in cheeks]].max())
            cheek_rest = {**dict.fromkeys(CHEEKS[:2], level(0, CHEEKS[:2])), **dict.fromkeys(CHEEKS[2:], level(0, CHEEKS[2:]))}
            for slot, cheeks in ((3, CHEEKS[:2]), (5, CHEEKS[2:])):
                if self.present[slot] and (full := level(slot, cheeks)) - cheek_rest[cheeks[0]] >= .2:
                    reach.update(dict.fromkeys(cheeks, full))
        brow_rest = {k: v for k, v in brow_rest.items() if not k.startswith(tuple(NATIVE_RAISE))}
        self.events = FaceEvents({"CheekPuffLeft": PUFF, "CheekPuffRight": PUFF, "CheekSuckLeft": SUCK, "CheekSuckRight": SUCK,
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

    def encode_enrollment(self, enrollment):
        """Face setup file -> mouth anchors (6, 512) + presence (6,), the one-cheek puff embeddings {side: (512,)}, brow
        neutral embedding (480,) + presence, and the brows' resting outputs (face_events' starting neutral); sets the
        wearer's tongue direction mapping from the held tongue poses. Runs once, at load."""
        self.tongue_map, holds = None, {}
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
            self.tongue_map = tongue_map(holds)
        return anchors, present, sides, (brow_neutral, brow_present), rest

    def update(self, strip, native=None, native_names=(), now_ns=0):
        q, t, w = self.run(strip)
        p = dict(zip(self.names, head_forward(self.head, q, self.anchors, self.present)))
        p.update(zip(BROWS, brow_forward(self.brow_head, w, *self.brow_neutral)))
        horizontal, vertical = tongue_direction(q, self.tongue_map) if self.tongue_map else np.tanh(t[1:3])
        fresh = fresh_values(native, native_names, now_ns)
        nv = fresh or {}
        native_puff = [nv.get(n, 0.) for n in NATIVE_PUFF]
        if self.puff_axis is not None:
            n, axis, unmix = self.puff_axis
            d = puff_depth(q, n, axis)
            if unmix is None:
                share = (d / max(float(d.max()), 1e-6)) ** 2
            else:
                d = np.clip(unmix @ d, 0., None)
                share = d / max(float(d.max()), 1e-6)
            amount = max(p["CheekPuffLeft"], p["CheekPuffRight"], *native_puff)
            p["CheekPuffLeft"], p["CheekPuffRight"] = amount * float(share[0]), amount * float(share[1])
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
