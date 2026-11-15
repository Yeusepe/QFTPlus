"""Own the shared headset eye session; all model restoration runs on the headset."""
from __future__ import annotations

from pathlib import Path
import queue
import secrets
import shlex
import subprocess
import threading
import time

from eye_trace import RawTraceEyeReader
from headset import ADB, NO_WINDOW, RootShell, adb


class IndependentEyeSession(RawTraceEyeReader):
    def __init__(self):
        super().__init__()
        self._ready = threading.Event()
        self._restored = False
        self._closed = threading.Event()
        self._safe_start_failure = False
        self._owner = None
        self._work = '/data/local/tmp/qft-eye-'+secrets.token_hex(12)

    def start(self) -> None:
        folder = Path(__file__).resolve().parent
        self._shell = RootShell()
        try:
            self._shell.run('mkdir -m 700 '+shlex.quote(self._work)+'; chown 2000:2000 '+shlex.quote(self._work))
            self._shell.undo_on_exit('rm -f '+self._work+'/*; rmdir '+self._work)
            for name in ('qft-eye-session','libqft_eye_swap.so','eye_swap.js'):
                adb('push', str(folder/name), self._work+'/'+name)
            self._shell.run('chown -R root:root '+self._work+'; chmod 755 '+self._work+' '+self._work+'/qft-eye-session; chmod 644 '+self._work+'/libqft_eye_swap.so')
            command=' '.join(shlex.quote(self._work+'/'+name) for name in ('qft-eye-session','libqft_eye_swap.so','eye_swap.js'))
            self._process=subprocess.Popen([ADB,'shell','su'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,text=True,encoding='utf-8',errors='replace',bufsize=1,creationflags=NO_WINDOW)
            self._process.stdin.write('exec '+command+'\n');self._process.stdin.flush()
            self._thread=threading.Thread(target=self._read_session,daemon=True);self._thread.start()
            if not self._ready.wait(25):
                raise RuntimeError('EYE_SWAP_START_FAILED')
            if not self.errors.empty():
                raise RuntimeError(self.errors.get_nowait())
        except Exception as error:
            try:self.close()
            except Exception as cleanup:
                print('EYE_SESSION_CLEANUP',repr(cleanup),flush=True)
                raise RuntimeError('EYE_SWAP_RESTORE_FAILED') from cleanup
            if str(error).startswith('EYE_SWAP_'):raise
            print('EYE_SESSION_DIAGNOSTIC',repr(error),flush=True)
            raise RuntimeError('EYE_SWAP_START_FAILED') from error

    def _read_session(self) -> None:
        from dataclasses import replace
        from eye_trace import DetectorOutputParser
        parser=DetectorOutputParser()
        try:
            for line in self._process.stdout:
                line=line.strip()
                if line=='QFT_EYES_READY':
                    self._ready.set()
                elif line.startswith('QFT_EYES_OWNER '):
                    self._owner=int(line.split()[-1])
                elif line in ('QFT_EYES_RESTORED','QFT_EYES_UNCHANGED'):
                    self._restored=True
                elif line=='QFT_EYES_CLOSED':
                    self._closed.set()
                elif line.startswith('QFT_EYES_ERROR '):
                    print(line,flush=True)
                    code=line.split()[-1]
                    if code in ('busy','unsupported','payload','service','trace_cleanup'):
                        self._safe_start_failure=True
                        self._closed.set()
                    self.errors.put_nowait('EYE_SWAP_'+{'busy':'BUSY','unsupported':'UNSUPPORTED'}.get(code,'START_FAILED'))
                    self._ready.set()
                elif 'detector_output' in line and not self._stopping:
                    now=time.monotonic_ns();sample=parser.parse(line,now)
                    if sample is not None:
                        sample=replace(sample,pc_monotonic_ns=self._clock.translate(sample.kernel_time_s,now))
                        if self.samples.full():
                            try:self.samples.get_nowait()
                            except queue.Empty:pass
                        self.samples.put_nowait(sample)
                elif line and 'detector_output' not in line:
                    print(line,flush=True)
            if not self._stopping and self.errors.empty():
                raise RuntimeError('EYE_SWAP_START_FAILED')
        except Exception as error:
            try:self.errors.put_nowait(str(error))
            except queue.Full:pass
        finally:self._ready.set()

    def close(self) -> None:
        self._stopping=True
        failure=None
        if self._process is not None:
            if self._process.stdin is not None:
                self._process.stdin.close()
            acknowledged=self._closed.wait(15)
            if not acknowledged and self._owner is not None and self._shell is not None:
                status=self._shell.run('cat /data/local/tmp/qft-eye-status-'+str(self._owner),check=False)
                if status and status.strip() in ('GUARD_RESTORED 0','GUARD_RESTORED 1'):
                    acknowledged=True;self._restored=True
            try:self._process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                self._process.terminate();self._process.wait(timeout=5)
                if not acknowledged:failure=RuntimeError('EYE_SWAP_RESTORE_FAILED')
            if self._thread is not None:self._thread.join(timeout=2)
            if not self._safe_start_failure and (not acknowledged or not self._restored):
                print('EYE_SESSION_EXIT',self._process.returncode,'restored',self._restored,flush=True)
                failure=RuntimeError('EYE_SWAP_RESTORE_FAILED')
            self._process=None
        if self._shell is not None:
            try:
                if self._owner is not None:self._shell.run('rm -f /data/local/tmp/qft-eye-status-'+str(self._owner),check=False)
                self._shell.run('rm -f '+self._work+'/*; rmdir '+self._work,check=False)
            finally:self._shell.close();self._shell=None
        if failure:raise failure
