"""Tester contribution: one benchmark recording -> PREFIX.contribution.zip, small enough to send by hand.

Inside: frames.npy (every frame, all five cameras area-resized to 128 px, uint8 -- what the face model sees), frame_ns.npy,
session.json (prompts and their times), labels.jsonl (Meta's own expression values), enrollment.npz (the face calibration's
held expressions, also 128 px) when given, and manifest.json (anonymous tester ID, consent version and time).
Never the full-size capture or the preview video.
Usage: contribution.py PREFIX --tester ID --consent-version N --consent-time ISO [--enrollment PATH]
"""
import argparse
import io
import json
import zipfile
from pathlib import Path
import numpy as np
from prepare_training import resize_cameras, scan_frames

SIZE = 128
FORMAT = "qftplus-contribution-v1"


def package(prefix, tester, consent_version, consent_time, enrollment=None, version=""):
    prefix = Path(prefix)
    capture, session, labels = (prefix.with_suffix(s) for s in (".qpcap", ".qpsession.json", ".qplabel.jsonl"))
    if not json.loads(session.read_text(encoding="utf-8")).get("sessionType", "").startswith("benchmark"):
        raise ValueError("Only benchmark recordings can be shared.")
    entries, _ = scan_frames(capture, 0x1F)
    out = prefix.with_name(prefix.name + ".contribution.zip")
    pending = out.with_name(out.name + ".tmp")
    with zipfile.ZipFile(pending, "w", zipfile.ZIP_DEFLATED, compresslevel=6, allowZip64=True) as z:
        with z.open("frames.npy", "w", force_zip64=True) as sink, capture.open("rb") as source:
            np.lib.format.write_array_header_1_0(sink, {"descr": "|u1", "fortran_order": False, "shape": (len(entries), 5, SIZE, SIZE)})
            for offset, _, width, height in entries:
                source.seek(offset)
                sink.write(resize_cameras(np.frombuffer(source.read(width * height), np.uint8).reshape(height, width), SIZE, 5).tobytes())
        buffer = io.BytesIO(); np.save(buffer, np.array([e[1] for e in entries], np.int64)); z.writestr("frame_ns.npy", buffer.getvalue())
        z.write(session, "session.json")
        if labels.exists():
            z.write(labels, "labels.jsonl")
        if enrollment and Path(enrollment).is_file():
            with np.load(enrollment, allow_pickle=False) as e:
                slots = {k: np.stack([resize_cameras(f, SIZE, 5) for f in e[k]]) for k in e.files if k.startswith("slot_")}
                buffer = io.BytesIO(); np.savez_compressed(buffer, meta=e["meta"], **slots); z.writestr("enrollment.npz", buffer.getvalue())
        z.writestr("manifest.json", json.dumps({
            "format": FORMAT, "tester": tester, "consentVersion": consent_version, "consentTime": consent_time,
            "appVersion": version, "recording": prefix.name, "frames": len(entries), "imageSize": SIZE, "cameras": 5,
            "enrollment": bool(enrollment and Path(enrollment).is_file())}, indent=1))
    pending.replace(out)
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("prefix"); ap.add_argument("--tester", required=True); ap.add_argument("--consent-version", type=int, required=True)
    ap.add_argument("--consent-time", required=True); ap.add_argument("--enrollment"); ap.add_argument("--version", default="")
    a = ap.parse_args()
    out = package(a.prefix, a.tester, a.consent_version, a.consent_time, a.enrollment, a.version)
    print(f"PACKAGE {out} {out.stat().st_size}", flush=True)


if __name__ == "__main__":
    main()
