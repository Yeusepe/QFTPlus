"""Run the existing tracking launchers for the lifetime of a VRCFT module."""

from __future__ import annotations

import argparse
import ctypes
import logging
import json
import os
from pathlib import Path
import subprocess
import time
import sys
from contextlib import ExitStack


def runtime_ready(root: Path, target: str) -> bool:
    try:
        steam = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq vrserver.exe", "/NH"],
            capture_output=True, timeout=5, creationflags=subprocess.CREATE_NO_WINDOW,
        )
        if b"vrserver.exe" not in steam.stdout.lower():
            return False
        command = [str(root / "platform-tools" / "adb.exe")]
        if target:
            command += ["-s", target]
        if ":" in target:
            state = subprocess.run(
                command + ["get-state"], capture_output=True, timeout=5,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            if state.returncode != 0 or state.stdout.strip() != b"device":
                subprocess.run(
                    [command[0], "connect", target], capture_output=True, timeout=5,
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )
        probe = subprocess.run(
            command + ["shell", "su", "-c", "id"], capture_output=True,
            timeout=5, creationflags=subprocess.CREATE_NO_WINDOW,
        )
        return probe.returncode == 0 and b"uid=0(root)" in probe.stdout
    except (OSError, subprocess.TimeoutExpired):
        return False


def supervise(root: Path, stop_file: Path, target: str, owner_alive, camera_arguments=(), *, quiet=False, tongue_models=None) -> None:
    children: list[subprocess.Popen] = []
    with ExitStack() as files:
        def running() -> bool:
            return owner_alive() and not stop_file.exists() and not (root / ".qpro-manual.stop").exists()

        def status(state: str) -> None:
            temporary = root / "autostart-status.json.tmp"
            try:
                temporary.write_text(json.dumps({"state": state, "pid": os.getpid(),
                                                "stopFile": str(stop_file)}), encoding="utf-8")
                temporary.replace(root / "autostart-status.json")
            except OSError:
                logging.warning("Status file busy; tracking continues")

        def launch(script: str, name: str, arguments: list[str]) -> None:
            command = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass",
                       "-File", str(root / script), *arguments, "-StopFile", str(stop_file)]
            if target:
                command += ["-AdbTarget", target]
            log = files.enter_context((root / f"autostart-{name}.log").open("w"))
            env = {key: value for key, value in os.environ.items()
                   if key.upper() != "PSMODULEPATH"}
            env.update(QPRO_PYTHON=sys.executable,
                       QPRO_ADB=str(root / "platform-tools" / "adb.exe"), PYTHONUNBUFFERED="1")
            children.append(subprocess.Popen(
                command, cwd=root, env=env, stdout=log, stderr=subprocess.STDOUT,
                creationflags=subprocess.CREATE_NO_WINDOW,
            ))
            logging.info("Started %s launcher pid=%s", name, children[-1].pid)

        try:
            logging.info("Waiting for rooted Quest and SteamVR")
            status("waiting")
            while running() and not runtime_ready(root, target):
                time.sleep(2)
            if not running():
                return
            launch("native-eye-local-branch-test.ps1", "eyes",
                   ["-RuntimePreview", "-VrcftOutput", "-VergenceGain", "1.0"] + (["-NoWindow"] if quiet else []))
            status("starting")
            for _ in range(7):
                if not running() or children[0].poll() is not None:
                    return
                time.sleep(1)
            if not running():
                return
            models = tongue_models or (root / "models" / "qpro-stereo-tongue-v8-gate.pt",
                                       root / "models" / "qpro-stereo-tongue-v8-direction.pt")
            launch("build-and-run.ps1", "tongue", [
                "-TonguePreview", "-EnableTongueOutput", "-AllCameras", "-StudioMode", "-SkipPythonSetup", "-MaxFps", "24",
                "-TongueModelPath", str(models[0]),
                "-TongueDirectionModelPath", str(models[1]),
            ] + list(camera_arguments))
            logging.info("Both launchers started; preview Q or module shutdown stops both")
            connected = False
            while running() and all(child.poll() is None for child in children):
                if not connected and "Connected. Keys" in (root / "autostart-tongue.log").read_text(errors="replace"):
                    status("running")
                    connected = True
                time.sleep(1)
        finally:
            logging.info("Requesting clean shutdown")
            stop_file.write_text("stop", encoding="utf-8")
            for child in children:
                child.wait()
                logging.info("Launcher pid=%s exited code=%s", child.pid, child.returncode)
            stop_file.unlink(missing_ok=True)
            status("stopped")
            logging.info("Runtime stopped")


def main() -> None:
    import msvcrt
    from ctypes import wintypes

    parser = argparse.ArgumentParser()
    parser.add_argument("--owner-pid", required=True, type=int)
    parser.add_argument("--stop-file", required=True, type=Path)
    parser.add_argument("--adb-target", default="")
    parser.add_argument("--pupil-preview", action="store_true")
    parser.add_argument("--pupil-dilation", action="store_true")
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--tongue-model", type=Path)
    parser.add_argument("--tongue-direction-model", type=Path)
    parser.add_argument("--extra-face-capture", choices=("cheeks", "brows", "lips", "nose", "jaw", "mouth"))
    parser.add_argument("--extra-face-model", type=Path)
    parser.add_argument("--extra-face-output", action="store_true")
    args = parser.parse_args()
    if bool(args.tongue_model) != bool(args.tongue_direction_model):
        parser.error("Both personal tongue checkpoints are required")
    root = Path(__file__).resolve().parent
    logging.basicConfig(filename=root / "autostart.log", level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    kernel.WaitForSingleObject.restype = wintypes.DWORD
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    owner = kernel.OpenProcess(0x00100000, False, args.owner_pid)
    if not owner:
        logging.info("Owning module already exited")
        return
    owner_alive = lambda: kernel.WaitForSingleObject(owner, 0) == 258
    try:
        with (root / ".qpro-autostart.lock").open("a+b") as lock:
            lock.seek(0)
            while owner_alive() and not args.stop_file.exists() and not (root / ".qpro-manual.stop").exists():
                try:
                    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
                    break
                except OSError:
                    time.sleep(1)
            else:
                return
            try:
                camera_arguments = []
                if args.pupil_preview:
                    camera_arguments += ["-PupilPreview"]
                if args.pupil_dilation:
                    camera_arguments += ["-EnablePupilDilation"]
                if args.extra_face_capture:
                    camera_arguments += ["-ExtraFaceCapture", args.extra_face_capture]
                if args.extra_face_model:
                    camera_arguments += ["-ExtraFaceModel", str(args.extra_face_model)]
                if args.extra_face_output:
                    camera_arguments += ["-EnableExtraFaceOutput"]
                if args.quiet and not args.extra_face_capture and not args.pupil_preview:
                    camera_arguments += ["-NoWindow"]
                models = (args.tongue_model, args.tongue_direction_model) if args.tongue_model else None
                supervise(root, args.stop_file, args.adb_target, owner_alive, camera_arguments,
                          quiet=args.quiet, tongue_models=models)
            finally:
                lock.seek(0)
                msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)
    except Exception:
        logging.exception("Automatic runtime failed")
    finally:
        kernel.CloseHandle(owner)


if __name__ == "__main__":
    main()
