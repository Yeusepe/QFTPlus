"""Runtime layer between model outputs and the VRCFT bridges. Pure numpy; the harness runs the same code offline.

Per frame, for each configured channel, in this order:
  1. neutral-anchored offset and bounded gain: clip((raw - neutral) * gain, 0, 1), gain = 1/(reach - neutral) in .7..1.5
  2. soft gates from Meta's native outputs (puff/suck need sealed lips and no tongue); stale or missing native passes
  3. mutually exclusive shapes (puff vs suck): the weaker one cannot fire
  4. speech-aware threshold raise / longer onset (speech = 2-7 Hz jaw motion in native JawDrop over 1 s; no microphone)
  5. event state machine: on above `on` held `hold_on` seconds, off below `off` held `hold_off` seconds
  6. One Euro smoothing of the intensity while on; 0 while off
Neutral refreshes slowly on quiet frames and resets after a donning gap. Unconfigured channels pass through unchanged.
Continuous channels (brows) skip 3-5: offset, gain, then smoothing on every frame; their neutral refreshes when Meta's native
brows are quiet (and the offset value is below .5).
"""
from collections import deque
import json
from pathlib import Path
from dataclasses import dataclass
import math
import numpy as np

NATIVE_MAX_AGE_NS = 100_000_000
DONNING_GAP_NS = 2_000_000_000
NEUTRAL_SECONDS = 20.
LOWER_FACE = ("Jaw", "Lip", "Mouth", "Cheek", "Chin", "Dimpler", "Tongue", "LowerLip", "UpperLip")
NATIVE_BROWS = ("InnerBrow", "OuterBrow", "BrowLowerer")
LIPS_APART, TONGUE_OUT = (.2, .4), (.3, .7)


@dataclass(frozen=True)
class Event:
    on: float
    off: float
    hold_on: float
    hold_off: float = 0.
    speech_raise: float = 0.
    speech_hold: float = 0.
    gate: str | None = None
    exclusive: str | None = None
    continuous: bool = False
    quiet: tuple = LOWER_FACE


PUFF = Event(.5, .35, .12, .15, speech_raise=.2, gate="sealed", exclusive="CheekSuck")
SUCK = Event(.5, .35, .12, .15, speech_raise=.2, gate="sealed", exclusive="CheekPuff")
TONGUE = Event(.5, .42, .15, speech_hold=.3)
BROW = Event(.5, .5, 0., continuous=True, quiet=NATIVE_BROWS)
EVENTS = {"CheekPuffLeft": PUFF, "CheekPuffRight": PUFF, "CheekSuckLeft": SUCK, "CheekSuckRight": SUCK, "TongueOut": TONGUE}


def ramp(value, low, high):
    return min(1., max(0., (value - low) / (high - low)))


class OneEuro:
    def __init__(self, min_cutoff=1.5, beta=.5, d_cutoff=1.):
        self.min_cutoff, self.beta, self.d_cutoff = min_cutoff, beta, d_cutoff
        self.x = self.dx = None

    @staticmethod
    def _alpha(cutoff, dt):
        return 1. / (1. + 1. / (2 * math.pi * cutoff * dt))

    def __call__(self, x, dt):
        if self.x is None or dt <= 0:
            self.x, self.dx = x, 0.
            return x
        dx = (x - self.x) / dt
        self.dx += self._alpha(self.d_cutoff, dt) * (dx - self.dx)
        self.x += self._alpha(self.min_cutoff + self.beta * abs(self.dx), dt) * (x - self.x)
        return self.x


class Speech:
    """Speaking moves the jaw at syllable rate: 2-7 Hz, i.e. 4-14 mean crossings a second, with some amplitude."""

    def __init__(self, window_ns=1_000_000_000):
        self.window = window_ns
        self.samples = deque()

    def add(self, t_ns, jaw):
        if self.samples and t_ns <= self.samples[-1][0]:
            return
        self.samples.append((t_ns, jaw))
        while self.samples[0][0] < t_ns - self.window:
            self.samples.popleft()

    def active(self, now_ns):
        s = [(t, v) for t, v in self.samples if t >= now_ns - self.window]
        if len(s) < 10:
            return False
        v = np.array([x for _, x in s]); v -= v.mean()
        seconds = (s[-1][0] - s[0][0]) / 1e9
        crossings = np.count_nonzero(np.diff(np.signbit(v)))
        return v.std() > .04 and seconds > .5 and 4 <= crossings / seconds <= 14


class FaceEvents:
    def __init__(self, events=None, *, neutral=None, reach=None, bias=0., log=None, log_max_bytes=1 << 30):
        """log: optional path of a .facelog.jsonl file (one line per frame: values in and out, events, speech, neutral;
        never images). Rotates to a new numbered file at log_max_bytes."""
        self.log_path, self.log_max, self.log_file, self.log_part = (Path(log) if log else None), log_max_bytes, None, 0
        self.config = dict(EVENTS if events is None else events)
        self.anchor_neutral = dict(neutral or {})
        self.reach = dict(reach or {})
        self.bias = float(np.clip(bias, -.1, .1))
        self.speech = Speech()
        self.speaking = False
        self.last_native_ns = None
        self.last_ns = None
        self.reset()

    def reset(self):
        """Donning or restart: neutral back to the enrollment value, every event off."""
        self.neutral = {n: self.anchor_neutral.get(n, 0.) for n in self.config}
        self.on = {n: False for n in self.config}
        self.since = {n: None for n in self.config}
        self.filters = {n: OneEuro() for n in self.config}

    def _native(self, sample, names, now_ns):
        if (sample is None or not names or sample.get("values") is None
                or abs(int(sample["arrivalMonotonicNs"]) - now_ns) > NATIVE_MAX_AGE_NS):
            return None
        return dict(zip(names, map(float, sample["values"])))

    def step(self, values, sample=None, names=(), now_ns=0):
        """values {name: raw}; sample = latest native label sample (or None); returns {name: output}."""
        dt = 0. if self.last_ns is None else max(0., (now_ns - self.last_ns) / 1e9)
        self.last_ns = now_ns
        native = self._native(sample, names, now_ns)
        if native is not None:
            arrival = int(sample["arrivalMonotonicNs"])
            if self.last_native_ns is not None and arrival - self.last_native_ns > DONNING_GAP_NS:
                self.reset()
            self.last_native_ns = arrival
            if "JawDrop" in native:
                self.speech.add(arrival, native["JawDrop"])
        self.speaking = native is not None and self.speech.active(now_ns)
        def at_rest(prefixes):
            v = [v for k, v in (native or {}).items() if k.startswith(prefixes)]
            return bool(v) and np.mean(np.array(v) < .15) >= .8
        quiet = {q: at_rest(q) for q in {e.quiet for e in self.config.values()}}

        post = {}
        for name, raw in values.items():
            if name not in self.config:
                continue
            n = self.neutral[name]
            gain = float(np.clip(1. / max(self.reach.get(name, 1.) - n, 1e-3), .7, 1.5))
            v = min(1., max(0., (float(raw) - n) * gain))
            if self.config[name].gate == "sealed" and native is not None and {"JawDrop", "LipsToward", "TongueOut"} <= native.keys():
                v *= 1. - max(ramp(native["JawDrop"] - native["LipsToward"], *LIPS_APART), ramp(native["TongueOut"], *TONGUE_OUT))
            post[name] = v
            e = self.config[name]
            settled = v < .5 if e.continuous else not self.on[name] and v < e.on and not self.speaking
            if quiet[e.quiet] and settled and dt:
                self.neutral[name] = float(np.clip(n + (float(raw) - n) * min(1., dt / NEUTRAL_SECONDS), 0., .5))

        out = dict(values)
        for name, v in post.items():
            e = self.config[name]
            if e.continuous:
                out[name] = self.filters[name](v, dt)
                continue
            if e.exclusive and v <= max((w for k, w in post.items() if k.startswith(e.exclusive)), default=-1.):
                v = 0.
            raise_ = e.speech_raise if self.speaking else 0.
            hold_on = max(e.hold_on, e.speech_hold) if self.speaking else e.hold_on
            on, off = e.on + raise_ + self.bias, e.off + raise_ + self.bias
            changing = v < off if self.on[name] else v >= on
            if not changing:
                self.since[name] = None
            elif self.since[name] is None:
                self.since[name] = now_ns
            if changing and (now_ns - self.since[name]) / 1e9 >= (e.hold_off if self.on[name] else hold_on):
                self.on[name] = not self.on[name]
                self.since[name] = None
                if not self.on[name]:
                    self.filters[name] = OneEuro()
            out[name] = self.filters[name](v, dt) if self.on[name] else 0.
        if self.log_path is not None:
            self._log(now_ns, values, out, sample, native is not None)
        return out

    def _log(self, now_ns, values, out, sample, fresh):
        if self.log_file is None or self.log_file.tell() > self.log_max:
            if self.log_file is not None:
                self.log_file.close(); self.log_part += 1
            path = self.log_path if not self.log_part else self.log_path.with_name(f"{self.log_path.stem}.{self.log_part}{self.log_path.suffix}")
            path.parent.mkdir(parents=True, exist_ok=True)
            self.log_file = path.open("a", encoding="utf-8", buffering=1 << 16)
        r = lambda d: {k: round(float(d[k]), 4) for k in self.config if k in d}
        self.log_file.write(json.dumps({"t": now_ns, "raw": r(values), "out": r(out), "on": [k for k in self.config if self.on[k]],
                                        "speaking": bool(self.speaking), "native": bool(fresh),
                                        "seq": sample.get("sourceSequence") if sample else None,
                                        "neutral": {k: round(v, 4) for k, v in self.neutral.items() if v}}) + "\n")

    def close(self):
        if self.log_file is not None:
            self.log_file.close(); self.log_file = None


def process_offline(raw, t_ns, native_values=None, native_names=(), native_t_ns=None, **kwargs):
    """raw {name: (T,)}, t_ns (T,); native_values (T,K) aligned to frames (NaN rows = stale) with their arrival times.

    Returns ({name: (T,) output}, {name: (T,) bool event}). Identical to calling step() frame by frame.
    """
    events = FaceEvents(**kwargs)
    names = list(raw)
    outs = {n: np.zeros(len(t_ns)) for n in names}
    on = {n: np.zeros(len(t_ns), bool) for n in names if n in events.config}
    for i, t in enumerate(map(int, t_ns)):
        sample = None
        if native_values is not None and np.isfinite(native_values[i]).all():
            sample = {"arrivalMonotonicNs": int(native_t_ns[i]), "values": native_values[i]}
        o = events.step({n: float(raw[n][i]) for n in names}, sample, native_names, t)
        for n in names:
            outs[n][i] = o[n]
        for n in on:
            on[n][i] = events.on[n]
    return outs, on
