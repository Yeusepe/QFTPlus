"""Control Meta interaction-gaze averaging without changing calibration or eye models."""
from __future__ import annotations

import argparse
from pathlib import Path
import secrets
import shlex
import socket
import subprocess
import threading

from headset import ADB, NO_WINDOW, RootShell, adb, stop_event

CONTROL_PORT = 27278


def samples(value):
    number = int(value)
    if str(number) != str(value).strip() or not 1 <= number <= 25:
        raise ValueError('GAZE_AVERAGING_INVALID')
    return number


class AveragingSession:
    def __init__(self, value):
        self.value = samples(value)
        self.root = None
        self.process = None
        self.ready = threading.Event()
        self.closed = threading.Event()
        self.restored = False
        self.failure = ''
        self.safe_start_failure = False
        self.reader = None
        self.work = '/data/local/tmp/qft-averaging-' + secrets.token_hex(12)

    def start(self):
        directory = Path(__file__).resolve().parent
        self.root = RootShell()
        try:
            self.root.run('mkdir -m 700 '+self.work+'; chown 2000:2000 '+self.work)
            self.root.undo_on_exit('rm -f '+self.work+'/*; rmdir '+self.work)
            for name in ('qft-eye-session', 'gaze_averaging.js'):
                adb('push', str(directory/name), self.work+'/'+name)
            self.root.run('chown -R root:root '+self.work+'; chmod 755 '+self.work+' '+self.work+'/qft-eye-session')
            command = shlex.join((self.work+'/qft-eye-session', '--averaging', self.work+'/gaze_averaging.js', str(self.value)))
            self.process = subprocess.Popen([ADB, 'shell', 'su -c '+shlex.quote(command)], stdin=subprocess.PIPE,
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace',
                bufsize=1, creationflags=NO_WINDOW)
            self.reader = threading.Thread(target=self._read, daemon=True)
            self.reader.start()
            if not self.ready.wait(25) or self.failure:
                raise RuntimeError(self.failure or 'GAZE_AVERAGING_START_FAILED')
        except Exception:
            self.close()
            raise

    def _read(self):
        for line in self.process.stdout:
            line = line.strip()
            if line == 'QFT_EYES_READY':
                self.ready.set()
            elif line == 'QFT_EYES_RESTORED':
                self.restored = True
            elif line == 'QFT_EYES_CLOSED':
                self.closed.set()
            elif line.startswith('QFT_EYES_ERROR '):
                code = line.split()[-1]
                self.safe_start_failure = code in ('busy', 'unsupported', 'service', 'payload', 'trace_cleanup')
                if self.safe_start_failure:self.closed.set()
                self.failure = 'GAZE_AVERAGING_'+{'busy':'BUSY','unsupported':'UNSUPPORTED'}.get(code,'START_FAILED')
                self.ready.set()
            elif line.startswith('QFT_AVERAGING_APPLIED '):
                print(line, flush=True)
            elif line:
                print(line, flush=True)
        if not self.closed.is_set():
            self.failure = self.failure or 'GAZE_AVERAGING_START_FAILED'
        self.ready.set()

    def update(self, value):
        self.process.stdin.write('samples:'+str(samples(value))+'\n')
        self.process.stdin.flush()

    def close(self):
        failure = None
        if self.process is not None:
            try:
                self.process.stdin.close()
                self.closed.wait(15)
                try:self.process.wait(timeout=1)
                except subprocess.TimeoutExpired:
                    self.process.terminate()
                    self.process.wait(timeout=5)
                if self.reader is not None:self.reader.join(timeout=2)
                if not self.safe_start_failure and (not self.restored or not self.closed.is_set()):
                    failure = RuntimeError('GAZE_AVERAGING_RESTORE_FAILED')
            except subprocess.TimeoutExpired:
                self.process.terminate()
                self.process.wait(timeout=5)
                failure = RuntimeError('GAZE_AVERAGING_RESTORE_FAILED')
            finally:
                self.process = None
        if self.root is not None:
            try:self.root.run('rm -f '+self.work+'/*; rmdir '+self.work, check=False)
            finally:self.root.close();self.root=None
        if failure:raise failure


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--samples', type=samples, default=25)
    args = parser.parse_args()
    stopped = stop_event()
    desired = [args.samples]
    control = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    control.bind(('127.0.0.1', CONTROL_PORT))
    def receive():
        while not stopped.is_set():
            try:desired[0] = samples(control.recv(64).decode('ascii'))
            except (ValueError, UnicodeError):continue
            except OSError:return
    threading.Thread(target=receive, daemon=True).start()
    print('QFT_AVERAGING_CONTROL_READY', flush=True)
    active = None
    attempted = None
    sent = None
    restore_failed = False
    try:
        while not stopped.is_set():
            value = desired[0]
            if restore_failed:
                stopped.wait(.1)
                continue
            try:
                if value == 25:
                    if active is not None:active.close();active=None
                    if sent != 25:print('QFT_AVERAGING_APPLIED 25',flush=True)
                    sent=25;attempted=None
                elif active is None and attempted != value:
                    attempted=value
                    active=AveragingSession(value);active.start();sent=value
                elif active is not None:
                    if active.failure:raise RuntimeError(active.failure)
                    if sent != value:active.update(value);sent=value
            except Exception as error:
                print('QFT_AVERAGING_ERROR '+str(error),flush=True)
                restore_failed = 'RESTORE_FAILED' in str(error)
                sent=None
                if active is not None:
                    try:active.close()
                    except Exception as cleanup:
                        restore_failed = True
                        print('QFT_AVERAGING_ERROR '+str(cleanup),flush=True)
                    active=None
                attempted=value
            stopped.wait(.1)
    finally:
        control.close()
        if active is not None:
            try:active.close()
            except Exception as error:
                restore_failed = True
                print('QFT_AVERAGING_ERROR '+str(error),flush=True)
    return 1 if restore_failed else 0


if __name__ == '__main__':
    raise SystemExit(main())
