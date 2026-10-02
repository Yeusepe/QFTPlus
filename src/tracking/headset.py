"""Shared plumbing for the processes QFT+ starts: headset adb and root access, the bundled frida-server, the stop signal.

QFT+ sets QPRO_ADB and ANDROID_SERIAL for every child, so adb here needs no device lookup. A child stops when QFT+
closes its stdin, which also happens if QFT+ itself exits, so nothing is left running.
"""
from __future__ import annotations

import hashlib
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

ADB = os.environ.get("QPRO_ADB", "adb")
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
FRIDA_SERVER = Path(__file__).resolve().parent / "hybrid" / "frida-server-17.18.0-android-arm64"


def stop_event() -> threading.Event:
    """Set once QFT+ closes this process's stdin."""
    stopped = threading.Event()
    def wait():
        try:
            sys.stdin.buffer.read()
        finally:
            stopped.set()
    threading.Thread(target=wait, name="stop-on-stdin-close", daemon=True).start()
    return stopped


def adb(*args: str, timeout: float = 30) -> str:
    return subprocess.check_output([ADB, *args], text=True, stderr=subprocess.STDOUT, timeout=timeout,
                                   creationflags=NO_WINDOW).strip()


class RootShell:
    """One live Magisk shell for many root commands.

    On the headset's Magisk build, a short `adb shell su -c ...` can finish on the headset while its PC client never
    sees it exit. One interactive root shell, with each reply ended by a unique marker, avoids that race.
    """

    def __init__(self) -> None:
        self._lines: queue.Queue[str] = queue.Queue()
        self._counter = 0
        self._process = subprocess.Popen(
            [ADB, "shell", "su"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", bufsize=1, creationflags=NO_WINDOW)
        threading.Thread(target=self._read, daemon=True).start()
        try:
            if "uid=0(root)" not in self.run("id"):
                raise RuntimeError("Magisk did not provide an interactive root shell")
        except BaseException:
            self.close()
            raise

    def _read(self) -> None:
        for line in self._process.stdout:
            self._lines.put(line.rstrip("\r\n"))

    def run(self, command: str, *, check: bool = True, timeout: float = 8.0) -> str:
        """Output of `command`; a nonzero exit raises unless check is False, then it returns None."""
        if self._process.poll() is not None:
            raise RuntimeError("The headset root shell exited")
        self._counter += 1
        marker = f"__QPRO_ROOT_DONE_{self._counter:08d}__:"
        self._process.stdin.write(f"{command}\necho {marker}$?\n")
        self._process.stdin.flush()
        output: list[str] = []
        deadline = time.monotonic() + timeout
        while (remaining := deadline - time.monotonic()) > 0:
            try:
                line = self._lines.get(timeout=min(remaining, 0.25))
            except queue.Empty:
                if self._process.poll() is not None:
                    raise RuntimeError("The headset root shell exited")
                continue
            if not line.startswith(marker):
                output.append(line)
                continue
            text = "\n".join(output)
            if line[len(marker):] == "0":
                return text
            if check:
                raise RuntimeError(f"Headset root command failed: {command}" + (f"\nHeadset response: {text}" if text else ""))
            return None
        if check:
            raise RuntimeError(f"Timed out waiting for the headset root shell: {command}")
        return None

    def close(self) -> None:
        if self._process.poll() is None:
            try:
                self._process.stdin.write("exit\n")
                self._process.stdin.flush()
                self._process.wait(timeout=2.0)
            except (BrokenPipeError, OSError, subprocess.TimeoutExpired):
                self._process.kill()
                self._process.wait()
        for pipe in (self._process.stdin, self._process.stdout):
            try:
                pipe.close()
            except OSError:
                pass


def start_frida_server(root: RootShell, name: str, port: int, *, reuse: bool = False):
    """Run the bundled frida-server as /data/local/tmp/<name> on the headset's 127.0.0.1:<port>, and connect to it
    through a free local port. Returns (frida device, local port).

    A <name> that is already running is refused, or with `reuse` adopted if it is the bundled build. The caller removes
    the forward and calls stop_frida_server; when connecting fails, this removes the forward and stops a server it
    started before raising.
    """
    import frida
    path = "/data/local/tmp/" + name
    bundled = hashlib.sha256(FRIDA_SERVER.read_bytes()).hexdigest()
    def installed() -> str:
        return (root.run(f"sha256sum {path} 2>/dev/null || true", timeout=15).split() or [""])[0]
    running = bool(root.run(f"pidof {name} || true", timeout=15).strip())
    if running and not reuse:
        raise RuntimeError(f"The headset helper {name} is already running. Retry after it finishes.")
    if installed() != bundled:
        if running:
            raise RuntimeError("The running headset helper differs from the bundled version. Restart the headset before retrying.")
        adb("push", str(FRIDA_SERVER), path, timeout=120)
        if installed() != bundled:
            raise RuntimeError("The headset helper failed verification")
    local = None
    try:
        if not running:
            root.run(f"chmod 755 {path} && {path} --listen 127.0.0.1:{port} --disable-preload --daemonize", timeout=15)
        local = int(adb("forward", "tcp:0", f"tcp:{port}"))
        device = frida.get_device_manager().add_remote_device(f"127.0.0.1:{local}")
        for attempt in range(20):
            try:
                device.enumerate_processes()
                return device, local
            except frida.TransportError:
                if attempt == 19:
                    raise
                time.sleep(0.25)
    except BaseException:
        try:
            if local is not None:
                adb("forward", "--remove", f"tcp:{local}")
            if not running:
                stop_frida_server(root, name)
        except Exception:
            pass
        raise


def stop_frida_server(root: RootShell, name: str) -> None:
    """Stop /data/local/tmp/<name> and delete it, so nothing of ours stays on the headset."""
    pids = root.run(f"pidof {name} || true", timeout=15).split()
    if pids:
        root.run("kill " + " ".join(str(int(pid)) for pid in pids), timeout=15)
    root.run(f"rm -f /data/local/tmp/{name}", timeout=15)
