"""Exact-frame, wearer-labeled experiments. No synthetic stock-derived labels."""
from __future__ import annotations

import textwrap
from pathlib import Path
import cv2
import numpy as np
from tongue_still_capture import TongueStillCaptureSession, TongueStillPrompt

FAMILIES = {
    "puff": ["CheekPuff"],
    "cheeks": ["CheekPuff", "CheekSuck"],
    "brows": ["BrowPinch", "BrowLowerer", "BrowInnerUp", "BrowOuterUp"],
    "pucker": ["LipPuckerUpper", "LipPuckerLower", "LipSuckCorner"],
    "corners": ["MouthUpperDeepen", "MouthCornerPull", "MouthCornerSlant"],
    "nose": ["NasalDilation", "NasalConstrict"],
    "jaw": ["JawBackward", "JawClench", "JawMandibleRaise"],
    "mouth": ["MouthUpperLeft", "MouthUpperRight", "MouthLowerLeft", "MouthLowerRight"],
}
STOCK_TRACKED = {base + side for base in ("BrowInnerUp", "BrowOuterUp", "CheekSuck")
                 for side in ("Left", "Right")}
INSTRUCTIONS = {
    "CheekPuff": "Fill the requested cheek with air. Keep the other cheek relaxed.",
    "CheekSuck": "Draw the requested cheek inward without pressing it with a hand.",
}
TITLES = {"CheekPuff": ("Puff your {side} cheek", "Puff both cheeks"),
          "CheekSuck": ("Suck your {side} cheek in", "Suck both cheeks in")}


def both(**values: float) -> dict[str, float]:
    return {base + side: value for base, value in values.items() for side in ("Left", "Right")}


POSES = {
    "brows": [
        ("Look worried", "Lift the middle of your brows, as if you're worried or sad.",
         both(BrowInnerUp=1, BrowOuterUp=0)),
        ("Look surprised", "Raise your eyebrows, as if something surprised you.",
         both(BrowInnerUp=1, BrowOuterUp=1, BrowPinch=0, BrowLowerer=0)),
        ("Frown", "Frown and pull your brows together, as if you're annoyed.",
         both(BrowPinch=1, BrowInnerUp=0, BrowOuterUp=0)),
        ("Squint into the sun", "Squint as if bright sunlight is in your eyes.",
         both(BrowLowerer=1, BrowInnerUp=0, BrowOuterUp=0)),
    ],
    "pucker": [
        ("Kiss", "Push your lips forward as if giving a kiss or saying “oo”.",
         both(LipPuckerUpper=1, LipPuckerLower=1, LipSuckCorner=0)),
        ("Pout", "Push out just your lower lip, as if sulking.",
         both(LipPuckerLower=1, LipPuckerUpper=0)),
        ("Skeptical “hmm”", "Keep your lips closed and tuck the corners in, as if you're unconvinced.",
         both(LipSuckCorner=1, LipPuckerUpper=0, LipPuckerLower=0)),
    ],
    "corners": [
        ("Big smile", "Smile widely with your lips apart.", both(MouthCornerPull=1)),
        ("Closed-mouth smile", "Smile with your lips closed, lifting just the corners.", both(MouthCornerSlant=1)),
        ("Smell something bad", "Wrinkle your nose and lift your upper lip, as if something smells bad.",
         both(MouthUpperDeepen=1, MouthCornerPull=0, MouthCornerSlant=0)),
    ],
    "mouth": [
        ("Mouth to your left", "Move your whole mouth toward your left, lips closed.",
         {"MouthUpperLeft": 1, "MouthLowerLeft": 1, "MouthUpperRight": 0, "MouthLowerRight": 0}),
        ("Mouth to your right", "Move your whole mouth toward your right, lips closed.",
         {"MouthUpperRight": 1, "MouthLowerRight": 1, "MouthUpperLeft": 0, "MouthLowerLeft": 0}),
        ("Jaw to your left", "Slide your lower jaw toward your left, lips closed.", {"MouthLowerLeft": 1, "MouthLowerRight": 0}),
        ("Jaw to your right", "Slide your lower jaw toward your right, lips closed.", {"MouthLowerRight": 1, "MouthLowerLeft": 0}),
    ],
    "nose": [
        ("Deep breath in", "Breathe in slowly and deeply through your nose.", both(NasalDilation=1, NasalConstrict=0)),
        ("Sharp sniff", "Sniff in quickly through your nose, and hold it.", both(NasalConstrict=1, NasalDilation=0)),
    ],
    "jaw": [
        ("Teeth together", "Close your mouth and rest your back teeth together, lips relaxed.",
         {"JawMandibleRaise": 1, "JawClench": 0, "JawBackward": 0}),
        ("Bite down", "Bite down on your back teeth, lips relaxed. Don't strain.",
         {"JawClench": 1, "JawMandibleRaise": 1, "JawBackward": 0}),
        ("Jaw back", "Gently slide your lower jaw back, like an overbite. Don't force it.", {"JawBackward": 1, "JawClench": 0}),
    ],
}
STRENGTH = {0.5: (" · halfway", "Gently, about halfway. "), 1.0: (" · full", "")}
ONLY_FULL = {"Teeth together"}


def pose_title(base: str, group: tuple[str, ...], strength: float) -> str:
    one, both_sides = TITLES[base]
    title = both_sides if len(group) == 2 else one.format(side="left" if group[0].endswith("Left") else "right")
    return title + STRENGTH[strength][0]


def target_names(family: str) -> list[str]:
    return [base + side for base in FAMILIES[family]
            for side in (("Left", "Right") if family not in ("jaw", "mouth") else ("",))]


def family_of(names) -> str:
    matches = [family for family in FAMILIES if names and set(names) <= set(target_names(family))]
    if not matches:
        raise ValueError("Not a known expression group")
    return min(matches, key=lambda family: len(FAMILIES[family]))


def make_prompts(family: str) -> list[TongueStillPrompt]:
    names = target_names(family)
    prompts = []
    for repetition in range(3):
        neutral = dict.fromkeys(names, 0.0)
        poses = [("Relax", "Let the air out.", neutral) if family == "puff" else
                 ("Relaxed", "Relax this whole region of your face.", neutral)]
        for strength in (0.5, 1.0):
            for title, instruction, targets in POSES.get(family, []):
                if strength < 1 and title in ONLY_FULL:
                    continue
                poses.append((title + STRENGTH[strength][0], STRENGTH[strength][1] + instruction,
                              {name: value * strength for name, value in targets.items()}))
            for base in FAMILIES[family] if family not in POSES else []:
                groups = [(base + "Left",), (base + "Right",), (base + "Left", base + "Right")]
                for group in groups:
                    targets = dict(neutral, **dict.fromkeys(group, strength))
                    instruction = INSTRUCTIONS[base]
                    if len(group) == 2 and base == "CheekPuff":
                        instruction = "Fill both cheeks with air equally."
                    elif len(group) == 2 and base == "CheekSuck":
                        instruction = "Draw both cheeks inward equally without pressing them with your hands."
                    title = pose_title(base, group, strength)
                    if base == "CheekPuff":
                        side = "Both cheeks" if len(group) == 2 else "Left cheek" if group[0].endswith("Left") else "Right cheek"
                        title = side + (" · halfway" if strength == 0.5 else " · full")
                        instruction = "Fill halfway." if strength == 0.5 else "Fill all the way."
                        if len(group) == 1:
                            instruction += " Other cheek flat."
                    poses.append((title, instruction, targets))
        for name, instruction, targets in poses:
            prompts.append(TongueStillPrompt(
                name=f"Round {repetition + 1}: {name}",
                instruction=instruction + " Skip with K if you cannot make this pose.",
                targets=targets, context=f"{family}/repetition-{repetition}",
                guide=name, recommended_captures=3 if family == "puff" else 4, minimum_captures=3))
    return prompts


class ExtraFaceCapture(TongueStillCaptureSession):
    def __init__(self, path: Path, family: str):
        super().__init__(path, prompts=make_prompts(family),
                         session_type="extra-face-stills-v1", title=f"Quest Pro: {family}",
                         capture_policy="one exact synchronized five-camera frame per Space press")

    def render(self, strip: np.ndarray, labels_ready: bool) -> np.ndarray:
        image = np.zeros((700, 1280, 3), dtype=np.uint8)
        def line(text, y, color=(225, 225, 225), size=0.65):
            cv2.putText(image, text, (20, y), cv2.FONT_HERSHEY_SIMPLEX,
                        size, color, 1, cv2.LINE_AA)
        line(self.title + " - local research capture", 35)
        line(f"{self.current_index + 1}/{len(self.prompts)}  {self.current.name}", 75, (80, 240, 120))
        for i in range(5):
            panel = cv2.resize(strip[:, i*400:(i+1)*400], (240, 240))
            image[100:340, 20+i*250:260+i*250] = cv2.cvtColor(panel, cv2.COLOR_GRAY2BGR)
        for i, text in enumerate(textwrap.wrap(self.current.instruction, 100)):
            line(text, 385+i*28, size=0.57)
        line("Left/right means YOUR side. Relax between stills, then reform the pose.", 495, size=0.56)
        line(f"Saved: {len(self.active_samples())}/4   SPACE: capture   ENTER: next   K: skip   X: undo   Q: quit", 545, size=0.53)
        line(self.message[:115], 590, (80, 220, 255), 0.5)
        line("These labels describe your attempted pose. They still require image review and holdout validation.", 640, size=0.5)
        return image
