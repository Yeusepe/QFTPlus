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
    "lips": ["LipPuckerUpper", "LipPuckerLower", "LipSuckCorner",
             "MouthUpperDeepen", "MouthCornerPull", "MouthCornerSlant"],
    "nose": ["NasalDilation", "NasalConstrict"],
    "jaw": ["JawBackward", "JawClench", "JawMandibleRaise"],
    "mouth": ["MouthUpperLeft", "MouthUpperRight", "MouthLowerLeft", "MouthLowerRight"],
}
INSTRUCTIONS = {
    "CheekPuff": "Fill the requested cheek with air. Keep the other cheek relaxed.",
    "CheekSuck": "Draw the requested cheek inward without pressing it with a hand.",
    "BrowPinch": "Pull the requested brow inward toward the nose, avoiding lowering it.",
    "BrowLowerer": "Lower the requested brow, avoiding pulling it toward the nose.",
    "BrowInnerUp": "Lift the inner end of the requested brow; relax its outer end.",
    "BrowOuterUp": "Lift the outer end of the requested brow; relax its inner end.",
    "LipPuckerUpper": "Push only the upper lip forward. Keep the lower lip relaxed.",
    "LipPuckerLower": "Push only the lower lip forward. Keep the upper lip relaxed.",
    "LipSuckCorner": "Roll the requested mouth corner inward, without sucking in your cheek.",
    "MouthUpperDeepen": "Deepen the upper-lip crease on the requested side, avoiding lifting the lip.",
    "MouthCornerPull": "Pull the requested mouth corner outward, avoiding lifting it.",
    "MouthCornerSlant": "Lift the requested mouth corner, avoiding pulling it outward.",
    "NasalDilation": "Flare the requested nostril. Do not touch your face.",
    "NasalConstrict": "Narrow the requested nostril. Do not touch your face.",
    "JawBackward": "Move the lower jaw backward gently. Do not force it.",
    "JawClench": "Gently tighten your jaw muscles, without moving the lips. Never force it.",
    "JawMandibleRaise": "Raise the lower jaw gently while keeping the lips relaxed.",
    "MouthUpperLeft": "Move only your upper lip toward your left.",
    "MouthUpperRight": "Move only your upper lip toward your right.",
    "MouthLowerLeft": "Move only your lower lip toward your left.",
    "MouthLowerRight": "Move only your lower lip toward your right.",
}


def target_names(family: str) -> list[str]:
    return [base + side for base in FAMILIES[family]
            for side in (("Left", "Right") if family not in ("jaw", "mouth") else ("",))]


def make_prompts(family: str) -> list[TongueStillPrompt]:
    names = target_names(family)
    prompts = []
    for repetition in range(3):
        neutral = dict.fromkeys(names, 0.0)
        poses = [("Relaxed", "Relax this whole region of your face.", neutral)]
        for base in FAMILIES[family]:
            groups = [(base,)] if family in ("jaw", "mouth") else [
                (base + "Left",), (base + "Right",), (base + "Left", base + "Right")]
            for group in groups:
                for strength in (0.5, 1.0):
                    targets = dict(neutral, **dict.fromkeys(group, strength))
                    instruction = INSTRUCTIONS[base]
                    if len(group) == 2 and base == "CheekPuff":
                        instruction = "Fill both cheeks with air equally."
                    elif len(group) == 2 and base == "CheekSuck":
                        instruction = "Draw both cheeks inward equally without pressing them with your hands."
                    title = " + ".join(group) + f" {strength:.0%}"
                    if base == "CheekPuff":
                        side = "both cheeks" if len(group) == 2 else "your left cheek" if group[0].endswith("Left") else "your right cheek"
                        title = "Puff " + side
                        instruction = "Halfway." if strength == 0.5 else "As much as comfortable."
                        if len(group) == 1:
                            instruction += " Keep the other cheek relaxed."
                    poses.append((title, instruction, targets))
        for name, instruction, targets in poses:
            prompts.append(TongueStillPrompt(
                name=f"Round {repetition + 1}: {name}",
                instruction=instruction + " Skip with K if you cannot isolate this pose.",
                targets=targets, context=f"{family}/repetition-{repetition}",
                guide=name, recommended_captures=4, minimum_captures=3))
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
