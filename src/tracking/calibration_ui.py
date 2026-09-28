"""Local, single-command mailbox for the native Windows calibration window."""
import json
import math
import re
import time
from pathlib import Path


class CalibrationUI:
    def __init__(self, prefix):
        self.prefix = Path(prefix)
        self.last_id = 0
        self.last_poll = self.last_publish = 0.0

    def take_key(self):
        now = time.monotonic()
        if now - self.last_poll < .1:
            return None
        self.last_poll = now
        try:
            command = json.loads(Path(str(self.prefix) + ".command.json").read_text(encoding="utf-8-sig"))
            number, key = command["id"], command["key"]
            if type(number) is int and number > self.last_id and key in (" ", "\r", "b", "x", "c"):
                self.last_id = number
                return key
        except (OSError, ValueError, KeyError, TypeError):
            pass
        return None

    def publish(self, still=None, pupil=None, ready=False, force=False):
        now = time.monotonic()
        if not force and now - self.last_publish < .2:
            return
        self.last_publish = now
        state = {"ack": self.last_id, "ready": ready, "time": time.time()}
        if still is not None:
            prompt = still.current
            state.update(kind="stills", index=still.current_index, total=len(still.prompts),
                         title=prompt.name, instruction=prompt.instruction,
                         count=len(still.active_samples()), recommended=prompt.recommended_captures,
                         minimum=prompt.minimum_captures, pending=still.pending_capture,
                         completed=still.completed)
            if set(prompt.targets) == {"CheekPuffLeft", "CheekPuffRight"}:
                left, right = prompt.targets["CheekPuffLeft"], prompt.targets["CheekPuffRight"]
                if left == right == 0:
                    state.update(title="Relax both cheeks", instruction="Let the air out and relax your face.")
                else:
                    side = "both cheeks" if left and right else "your left cheek" if left else "your right cheek"
                    amount = "halfway" if max(left, right) == .5 else "fully"
                    state.update(title=f"Puff {side} {amount}", instruction="Relax, then form this pose again before each capture.")
        if pupil is not None:
            from pupil_dilation import DURATION
            phase = pupil.phase
            state.update(kind="pupils", phase=phase, result=pupil.result,
                         message=pupil.message, level=pupil.level,
                         remaining=max(0, math.ceil(DURATION - (now - pupil.started))) if phase is not None else 0,
                         completed=pupil.result == "passed")
        path = Path(str(self.prefix) + ".state.json")
        path.parent.mkdir(parents=True, exist_ok=True)
        temporary = path.with_suffix(".tmp")
        temporary.write_text(json.dumps(state), encoding="utf-8")
        for attempt in range(20 if force else 3):
            try:
                temporary.replace(path)
                return
            except PermissionError:
                time.sleep(.01)


def describe(name):
    words = re.findall("[A-Z][a-z]*", name)
    side = [words.pop().lower()] if words[-1] in ("Left", "Right") else []
    return " ".join(side + [word.lower() for word in words])


def check(checkpoint):
    from extra_face_capture import family_of
    names = checkpoint.get("expressionNames", [])
    if checkpoint.get("schema") != "extra-face-stills-v1":
        raise ValueError("Expected an extra-face model")
    puff = family_of(names) == "puff"
    failed, reasons = False, []
    for name in sorted(names):
        score = checkpoint.get("validation", {}).get(name, {})
        values = [score.get(k) for k in ("mae", "neutralMaximum", "fullMinimum")]
        what = describe(name).replace("cheek puff", "cheek") if puff else describe(name)
        if (not score.get("hasNeutralAndFull") or score.get("count", 0) < 10
                or any(not isinstance(v, (int, float)) or not math.isfinite(v) for v in values)):
            failed = True
            reasons.append(f"Some {what} poses were skipped.")
            continue
        if values[0] <= .20 and values[1] <= .25 and values[2] >= .75:
            continue
        failed = True
        if (score.get("oppositeOnlyMaximum") or 0) > .25:
            reasons.append(f"Your {what} filled up when only the other one should have." if puff
                           else f"Your {what} moved when only the other side should have.")
        elif values[1] > .25:
            reasons.append(f"Your {what} looked puffed while relaxed." if puff else f"Your {what} looked active while relaxed.")
        if values[2] < .75:
            reasons.append(f"Full {what.replace('cheek', 'puffs')} looked partial. Fill all the way." if puff
                           else f"Full {what} poses looked partial. Go all the way.")
        if (score.get("halfMean") or 0) > .8:
            reasons.append("Halfway puffs looked like full ones. Use about half the air." if puff
                           else "Halfway poses looked like full ones. Go about half as far.")
    if failed:
        raise ValueError(("Cheek calibration" if puff else "This calibration") + " didn't pass its test round. "
                         + " ".join(dict.fromkeys(reasons))
                         + " Try again. Your previous calibration is still in use.")


if __name__ == "__main__":
    import argparse
    import torch
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("check", "approve"))
    parser.add_argument("model", type=Path)
    args = parser.parse_args()
    model = torch.load(args.model, map_location="cpu", weights_only=True)
    try:
        check(model)
    except ValueError as error:
        parser.exit(1, str(error) + "\n")
    if args.action == "approve":
        model["approvedForOutput"] = True
        pending = args.model.with_suffix(".tmp")
        torch.save(model, pending)
        pending.replace(args.model)
