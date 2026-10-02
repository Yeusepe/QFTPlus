"""Timed guided sessions on the live camera stream: the tester benchmark and the one-minute face enrollment.

A session is a list of steps. Each step shows a prompt for a fixed time; hold steps ramp their face-guide targets
in over `ramp` seconds. Time stops while paused. The session file records when each step really started and ended
(PC monotonic ns, the same clock as the capture frames and native labels), so frames are labelled by time.
"""
from dataclasses import dataclass, field, replace
import json
from pathlib import Path
import random
import sys
import time
import numpy as np


@dataclass(frozen=True)
class Step:
    name: str
    instruction: str
    seconds: float
    condition: str
    targets: dict = field(default_factory=dict)
    ramp: float = 0.
    slot: str | None = None
    record_hz: float = 0.
    native: dict = field(default_factory=dict)
    soft: bool = False
    optional: bool = False


PUFF = {"both": {"CheekPuffLeft": 1., "CheekPuffRight": 1.}, "left": {"CheekPuffLeft": 1.}, "right": {"CheekPuffRight": 1.}}
TONGUE = {"out": (0., 0.), "left": (-1., 0.), "right": (1., 0.), "up": (0., 1.), "down": (0., -1.)}
TARGETS = ([(f"CheekPuff{s.title() if s != 'both' else 'Both'}", f"Puff {'both cheeks' if s == 'both' else f'your {s} cheek'}",
             "Fill with air and keep your lips sealed.", t) for s, t in PUFF.items()]
           + [("CheekSuck", "Suck in your cheeks", "Pull both cheeks in between your teeth.", {"CheekSuckLeft": 1., "CheekSuckRight": 1.})]
           + [(f"Tongue{d.title()}", "Stick your tongue out" + ("" if d == "out" else f", pointing {d}"),
               "Your left and right, as you feel it." if d in ("left", "right") else "Point it as far as feels comfortable.",
               {"visibility": 1., "extension": 1., "horizontal": h, "vertical": v})
              for d, (h, v) in TONGUE.items()]
           + [("NasalDilation", "Flare your nostrils", "If you can't flare them, breathe in slowly and deeply through your nose.",
               {"NasalDilationLeft": 1., "NasalDilationRight": 1.}),
              ("JawOpen", "Open your mouth wide", "Then close it fully between holds.", {})]
           + [("BrowRaiseBoth", "Raise both eyebrows", "As if surprised.",
               {"BrowInnerUpLeft": 1., "BrowInnerUpRight": 1., "BrowOuterUpLeft": 1., "BrowOuterUpRight": 1.}),
              ("BrowRaiseLeft", "Raise only your left eyebrow", "Keep the right one still if you can. If you can't, just try.",
               {"BrowInnerUpLeft": 1., "BrowOuterUpLeft": 1.}),
              ("BrowRaiseRight", "Raise only your right eyebrow", "Keep the left one still if you can. If you can't, just try.",
               {"BrowInnerUpRight": 1., "BrowOuterUpRight": 1.}),
              ("BrowInnerUp", "Look worried", "Lift the middle of your brows, as if worried or sad.",
               {"BrowInnerUpLeft": 1., "BrowInnerUpRight": 1.}),
              ("BrowFrown", "Frown", "Pull your brows down and together, as if annoyed.",
               {"BrowLowererLeft": 1., "BrowLowererRight": 1., "BrowPinchLeft": 1., "BrowPinchRight": 1.})])
AMOUNTS = (("Barely", .2), ("A little", .4), ("Halfway", .6), ("Most of the way", .8), ("Full", 1.))
READ = ["The thick path through the thirty thin birch trees bent north, then south, then back again.",
        "Bob put the big map by the pump, but Pam mopped the mud before it dried on the mat.",
        "Father thought the weather would be fine, though the clouds gathered over the three hills.",
        "My mom made a muffin mix, then baked a batch of plum buns for the bake sale by the bay.",
        "Open the window wide; the cool autumn air comes over the water and through the hall.",
        "Thank them both for the bath towels; they brought them through the thunder this Thursday."]
LOOKALIKES = [("chewing", "Chew", "Chew slowly, as if you had gum, with your lips closed."),
              ("tongue_bulge_left", "Push your tongue into your left cheek", "Lips closed; make a bump from the inside."),
              ("tongue_bulge_right", "Push your tongue into your right cheek", "Lips closed; make a bump from the inside."),
              ("lip_suck", "Suck your lips in", "Press both lips in between your teeth."),
              ("pout", "Pout", "Push your lips forward like a sulking child."),
              ("lip_shrug", "Push your lower lip out", "A doubtful, 'not sure' face."),
              ("nose_wrinkle", "Wrinkle your nose", "As if something smells bad."),
              ("sniff", "Sniff a few times", "Short sniffs through your nose."),
              ("jaw_side", "Move your jaw side to side", "Slowly, teeth apart."),
              ("jaw_forward", "Push your jaw forward", "Then back, a few times."),
              ("yawn", "Yawn", "A big, slow yawn, or fake one."),
              ("laugh", "Laugh", "Laugh out loud, as naturally as you can."),
              ("smile", "Smile and talk", "Big smiles, then a few words."),
              ("swallow", "Swallow and lick your lips", "Swallow a few times, then run your tongue along your lips."),
              ("eye_squint", "Squint", "As if looking into bright sun."),
              ("look_up_down", "Look up and down", "With your eyes only; keep your head still."),
              ("blink", "Blink, then close your eyes", "Blink a few times, then keep your eyes closed for a moment.")]
GUIDE = {"sweep:tongue": {"visibility": 1., "extension": .8, "horizontal": .7, "vertical": .4},
         "sweep:jaw": {"MouthUpperLeft": 2., "MouthLowerLeft": 2.}, "speech:read": {"MouthOpen": .35}, "speech:talk": {"MouthOpen": .35},
         "lookalike:tongue_bulge_left": {"TongueBulgeLeft": 1.5}, "lookalike:tongue_bulge_right": {"TongueBulgeRight": 1.5},
         "lookalike:lip_suck": {"LipSuck": 1.}, "lookalike:pout": {"LipPuckerLowerLeft": 1., "LipPuckerLowerRight": 1.},
         "lookalike:lip_shrug": {"LipPuckerLowerLeft": .6, "LipPuckerLowerRight": .6},
         "lookalike:nose_wrinkle": {"NasalConstrictLeft": 1., "NasalConstrictRight": 1., "BrowLowererLeft": .5, "BrowLowererRight": .5},
         "lookalike:sniff": {"NasalDilationLeft": .6, "NasalDilationRight": .6}, "lookalike:jaw_side": {"MouthUpperLeft": 2., "MouthLowerLeft": 2.},
         "lookalike:jaw_forward": {"JawBackward": -2.}, "lookalike:yawn": {"MouthOpen": 1., "EyeClosed": .6},
         "lookalike:laugh": {"MouthSmile": 1., "MouthOpen": .5, "EyeSquint": .6}, "lookalike:smile": {"MouthSmile": 1.},
         "lookalike:chewing": {"MouthOpen": .1}, "lookalike:swallow": {"visibility": 1., "extension": .3, "horizontal": .8, "vertical": .5},
         "lookalike:eye_squint": {"EyeSquint": 1., "BrowLowererLeft": .4, "BrowLowererRight": .4},
         "lookalike:look_up_down": {"LookUp": 1.}, "lookalike:blink": {"EyeClosed": 1.}}


QUICK_TARGETS = {"CheekPuffBoth", "CheekPuffLeft", "CheekPuffRight", "CheekSuck", "TongueOut", "TongueLeft", "TongueRight",
                 "BrowRaiseBoth", "BrowRaiseLeft", "BrowRaiseRight", "BrowFrown"}
QUICK_LOOKALIKES = {"chewing", "tongue_bulge_left", "tongue_bulge_right", "lip_suck", "pout", "smile", "swallow", "eye_squint"}


def benchmark_steps(seed, quick=False):
    """About 18 minutes (quick: about 5, with every brow prompt but "look worried"). Neutral first, free play last, the other blocks in a random order per session.

    quick keeps the puff/suck/tongue targets at halfway..full, the closest look-alikes and 40 s of reading.
    """
    rng = random.Random(seed)
    targets = []
    for name, title, instruction, guide in TARGETS:
        if quick and name not in QUICK_TARGETS:
            continue
        for label, amount in AMOUNTS[2:] if quick else AMOUNTS:
            targets.append(Step(f"{title} · {label.lower()}", instruction, 2.5, f"target:{name}",
                                {k: (v * amount if k not in ("horizontal", "vertical", "visibility") else v) for k, v in guide.items()},
                                ramp=1.))
            targets.append(Step("Relax", "Let your face go loose.", 1.5, "relax"))
    read = [Step("Read this out loud, again and again until the timer ends", text, 20., "speech:read") for text in (READ[:2] if quick else READ)]
    talk = [] if quick else [Step("Talk about your day", "Out loud, as if to a friend. Keep going until the timer ends.", 120., "speech:talk")]
    looks = [Step(title, instruction, 15. if quick else 20., f"lookalike:{name}") for name, title, instruction in LOOKALIKES
             if not quick or name in QUICK_LOOKALIKES]
    blocks = [targets, read, talk, looks]
    rng.shuffle(blocks)
    return ([Step("Relax and look around", "Keep your face relaxed and still; move your eyes and head normally.", 20. if quick else 60., "neutral")]
            + [s for b in blocks for s in b]
            + ([] if quick else [Step("Free play", "Use your face however you like: talk, react, make faces.", 120., "free")]))



class GuidedSession:
    def __init__(self, path, steps, session_type, now_ns, **meta):
        self.path, self.steps, self.session_type = path, list(steps), session_type
        self.index, self.elapsed, self.last_ns = 0, 0., now_ns
        self.completed = False
        self.record = {"version": 1, "sessionType": session_type, "completed": False, "startedMonotonicNs": now_ns,
                       "startedWallNs": time.time_ns(), **meta, "pauses": [],
                       "steps": [{"name": s.name, "instruction": s.instruction, "condition": s.condition, "targets": s.targets,
                                  "plannedSeconds": s.seconds, "startNs": None, "endNs": None, "skipped": False} for s in self.steps]}
        self.record["steps"][0]["startNs"] = now_ns
        self.paused_since = None
        self._save()

    @property
    def current(self):
        return self.steps[min(self.index, len(self.steps) - 1)]

    def update(self, now_ns, paused):
        """Advance the clock; returns True when the step changed."""
        if self.completed:
            return False
        if paused:
            if self.paused_since is None:
                self.paused_since = now_ns
            self.last_ns = now_ns
            return False
        if self.paused_since is not None:
            self.record["pauses"].append([self.paused_since, now_ns])
            self.paused_since = None
        self.elapsed += (now_ns - self.last_ns) / 1e9
        self.last_ns = now_ns
        if self.elapsed < self.current.seconds:
            return False
        self._advance(now_ns, skipped=False)
        return True

    def skip(self, now_ns):
        if not self.completed:
            self._advance(now_ns, skipped=True)

    def _advance(self, now_ns, skipped):
        step = self.record["steps"][self.index]
        step["endNs"], step["skipped"] = now_ns, skipped
        self.index += 1
        self.elapsed = 0.
        if self.index >= len(self.steps):
            self.completed = True
        else:
            self.record["steps"][self.index]["startNs"] = now_ns
        self._save()

    def ui(self):
        s = self.current
        forming = 1. if s.ramp <= 0 else min(1., self.elapsed / s.ramp)
        remaining = max(0., s.seconds - self.elapsed)
        return {"title": s.name, "instruction": s.instruction, "index": self.index, "total": len(self.steps),
                "targets": {k: (v if k in ("horizontal", "vertical", "visibility") else v * forming)
                            for k, v in {**GUIDE.get(s.condition, {}), **s.targets}.items()},
                "progress": (self.index + min(1., self.elapsed / s.seconds)) / len(self.steps), "remaining": remaining,
                "cue": f"{remaining:.0f} s" if s.seconds >= 10 else ("Hold" if forming >= 1 else "Get ready") if s.ramp else ""}

    def finish(self, completed, now_ns=None):
        now_ns = now_ns or time.monotonic_ns()
        if self.paused_since is not None:
            self.record["pauses"].append([self.paused_since, now_ns]); self.paused_since = None
        if not self.completed and self.record["steps"][min(self.index, len(self.steps) - 1)]["endNs"] is None:
            self.record["steps"][min(self.index, len(self.steps) - 1)]["endNs"] = now_ns
        self.record.update(completed=completed, finishedMonotonicNs=now_ns)
        self._save()

    def note_frame(self, frame_index, t_ns):
        """A frame was saved during the current step (enrollment saves only some)."""
        s = self.current
        self.record["steps"][self.index].setdefault("frames", []).append([frame_index, t_ns, self.elapsed >= s.ramp])

    def _save(self):
        pending = self.path.with_suffix(".tmp")
        pending.write_text(json.dumps(self.record, indent=1), encoding="utf-8")
        pending.replace(self.path)



ENROLL_CAP_SECONDS = 90.
SLOW = 2.
RELAX = Step("Relax", "Let your face go loose.", 1., "relax")


def enrollment_steps(pace=1.):
    """About 50 s (slow mode: pace=SLOW, about 100 s): neutral, seven ramp-and-hold anchors (1 s in, 2 s hold, 1 s relax),
    two sweeps, optional reading. The one-cheek puffs replace the old puff sweep, whose frames nothing read."""
    def anchor(slot, title, instruction, targets=None, native=None, soft=False):
        return [Step(title, instruction, 3., f"anchor:{slot}", targets or {}, ramp=1., slot=slot, record_hz=4.,
                     native=native or {}, soft=soft), RELAX]
    steps = ([Step("Relax and look ahead", "Keep your face still and relaxed.", 3., "neutral", slot="neutral", record_hz=4.)]
            + anchor("jaw_open", "Open your mouth wide", "As wide as is comfortable.", native={"JawDrop": .4})
            + anchor("pucker", "Kiss", "Push your lips forward into a kiss.",
                     {f"LipPucker{p}{s}": 1. for p in ("Upper", "Lower") for s in ("Left", "Right")}, native={"LipPucker": .3})
            + anchor("puff", "Puff both cheeks", "Fill with air, lips sealed.", {"CheekPuffLeft": 1., "CheekPuffRight": 1.},
                     native={"CheekPuff": .2}, soft=True)
            + anchor("puff_left", "Puff only your left cheek", "Move the air into your left cheek, lips sealed.", {"CheekPuffLeft": 1.},
                     native={"CheekPuffL": .2}, soft=True)
            + anchor("puff_right", "Puff only your right cheek", "Move the air into your right cheek, lips sealed.", {"CheekPuffRight": 1.},
                     native={"CheekPuffR": .2}, soft=True)
            + anchor("tongue_out", "Stick your tongue out", "Straight out, as far as is comfortable.",
                     {"visibility": 1., "extension": 1., "horizontal": 0., "vertical": 0.}, native={"TongueOut": .5})
            + anchor("suck", "Suck in your cheeks", "Pull both cheeks in between your teeth.", {"CheekSuckLeft": 1., "CheekSuckRight": 1.},
                     native={"CheekSuck": .2}, soft=True)
            + [Step("Tongue in a circle", "Slowly circle your tongue outside your lips.", 6., "sweep:tongue", record_hz=2.),
               Step("Jaw side to side", "Slowly, teeth apart.", 6., "sweep:jaw", record_hz=2.),
               Step("Read this out loud", READ[0], 9., "speech:read", record_hz=2., optional=True)])
    return [paced(s, pace) for s in steps]


def paced(step, pace):
    return replace(step, seconds=step.seconds * pace, ramp=step.ramp * pace)


def mouth_roi(strip):
    """Mouth cameras 2 and 3 of a 400x2000 strip, block-averaged to 25x50: cheap enough per frame during a hold."""
    return strip[:, 800:1600].reshape(25, 16, 50, 16).mean((1, 3), dtype=np.float32)


class EnrollmentSession(GuidedSession):
    """Checks every anchor hold; a failed hold is retried once with the reason, then skipped. Never exceeds the cap."""

    def __init__(self, path, steps, now_ns, pace=1., **meta):
        super().__init__(path, steps, "face-enrollment-v1", now_ns, capSeconds=ENROLL_CAP_SECONDS * pace, pace=pace, **meta)
        self.observed, self.neutral_roi, self.retried, self.pace = [], None, set(), pace

    def observe(self, strip, native, frozen):
        """native: {name: value} from a fresh native sample, or None."""
        s = self.current
        if s.slot and self.elapsed >= s.ramp and not self.completed:
            roi = mouth_roi(strip)
            self.observed.append((roi, native, frozen, float(roi.mean(dtype=np.float64))))

    def evaluate(self, step):
        obs, result = self.observed, {"frames": len(self.observed)}
        if len(obs) < 3:
            return {**result, "passed": False, "reason": "The headset cameras didn't send images."}
        rois = np.stack([o[0] for o in obs])
        result["frozen"] = float(np.mean([o[2] for o in obs]))
        result["intensity"] = float(np.mean([o[3] for o in obs]))
        if result["frozen"] > .3:
            return {**result, "passed": False, "reason": "The camera image froze."}
        if not 10 <= result["intensity"] <= 230:
            return {**result, "passed": False, "reason": "The mouth cameras are too dark or too bright."}
        reference = rois.mean(0) if step.slot == "neutral" else self.neutral_roi
        if reference is not None:
            d = np.abs(rois - reference).mean((1, 2))
            result["distance"], result["spread"] = float(np.median(d)), float(d.std())
            if step.slot != "neutral" and result["distance"] < 3:
                return {**result, "passed": False, "reason": "Your face looked the same as when relaxed."}
            if result["spread"] > max(2.5, .5 * result["distance"]):
                return {**result, "passed": False, "reason": "Try to hold still."}
        for prefix, minimum in step.native.items():
            values = [max(v for k, v in n.items() if k.startswith(prefix)) for _, n, _, _ in obs
                      if n and any(k.startswith(prefix) for k in n)]
            if values:
                result.setdefault("native", {})[prefix] = float(np.median(values))
                if np.median(values) < minimum and not step.soft:
                    return {**result, "passed": False, "reason": "Face tracking didn't see it clearly."}
        return {**result, "passed": True}

    def _advance(self, now_ns, skipped):
        step, entry = self.current, self.record["steps"][self.index]
        elapsed_total = ((now_ns - self.record["startedMonotonicNs"]) / 1e9 - sum((b - a) / 1e9 for a, b in self.record["pauses"])) / self.pace
        if step.slot:
            gate = {"passed": False, "reason": "skipped"} if skipped else self.evaluate(step)
            entry["gate"] = gate
            if gate["passed"] and step.slot == "neutral":
                self.neutral_roi = np.stack([o[0] for o in self.observed]).mean(0)
            if not gate["passed"] and not skipped and step.slot not in self.retried and elapsed_total < ENROLL_CAP_SECONDS - 10:
                self.retried.add(step.slot)
                retry = replace(step, name="Once more: " + step.name, instruction=gate["reason"] + " " + step.instruction)
                self._insert(self.index + 1, [retry, paced(RELAX, self.pace)])
        self.observed = []
        if elapsed_total > ENROLL_CAP_SECONDS - 15:
            for i in range(len(self.steps) - 1, self.index, -1):
                if self.steps[i].optional:
                    del self.steps[i]; del self.record["steps"][i]
        super()._advance(now_ns, skipped)

    def _insert(self, at, steps):
        self.steps[at:at] = steps
        self.record["steps"][at:at] = [{"name": s.name, "instruction": s.instruction, "condition": s.condition, "targets": s.targets,
                                        "plannedSeconds": s.seconds, "startNs": None, "endNs": None, "skipped": False}
                                       for s in steps]


def check(prefix):
    """Build PREFIX.anchors.npz from an enrollment capture. Neutral is the only required anchor."""
    from capture_format import scan_frames
    prefix = Path(prefix)
    session = json.loads(prefix.with_suffix(".qpsession.json").read_text(encoding="utf-8"))
    if session.get("sessionType") != "face-enrollment-v1":
        raise ValueError("This isn't a face setup recording.")
    entries = scan_frames(prefix.with_suffix(".qpcap"), 0x1F)
    with prefix.with_suffix(".qpcap").open("rb") as capture:
        def frames(indices):
            out = []
            for i in indices[-8:]:
                offset, _, w, h = entries[i]
                capture.seek(offset)
                out.append(np.frombuffer(capture.read(w * h), np.uint8).reshape(h, w))
            return np.stack(out)
        arrays, slots, gates = {}, {}, {}
        for step in session["steps"]:
            slot = step["condition"].split(":", 1)[1] if step["condition"].startswith("anchor:") else (
                "neutral" if step["condition"] == "neutral" else None)
            if slot and "gate" in step:
                gates.setdefault(slot, []).append(step["gate"])
                held = [f[0] for f in step.get("frames", []) if f[2]]
                if step["gate"]["passed"] and held and slot not in slots:
                    arrays["slot_" + slot] = frames(held); slots[slot] = len(held)
        sweep = [f[0] for s in session["steps"] if s["condition"].startswith("sweep:") for f in s.get("frames", [])]
        if sweep:
            arrays["sweep"] = np.stack([frames([i])[0] for i in sweep])
    if "neutral" not in slots:
        raise ValueError("The relaxed-face part didn't record cleanly. Run the face setup again.")
    meta = {"schema": "face-enrollment-v1", "slots": sorted(slots), "missing": sorted(set(gates) - set(slots)), "gates": gates,
            "seconds": (session.get("finishedMonotonicNs", 0) - session["startedMonotonicNs"]) / 1e9,
            "session": prefix.with_suffix(".qpsession.json").name}
    out = prefix.with_suffix(".anchors.npz")
    np.savez_compressed(out, meta=np.array(json.dumps(meta)), **arrays)
    return out, meta


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "check":
        try:
            path, meta = check(sys.argv[2])
        except (OSError, ValueError, KeyError) as error:
            print(error)
            sys.exit(1)
        print(f"ANCHORS_READY path={path} slots={','.join(meta['slots'])} missing={','.join(meta['missing'])}")
    else:
        sys.exit("usage: guided_session.py check PREFIX")
