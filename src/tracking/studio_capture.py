"""Calibration on the existing camera stream; never starts a headset service."""
import json
from pathlib import Path
import time
import uuid
import zlib
import cv2
from capture_format import CaptureWriter
from guided_session import SLOW, EnrollmentSession, GuidedSession, benchmark_steps, enrollment_steps


def publish(path, value):
    """A busy status reader must not tear down the headset session."""
    pending = path.with_suffix('.tmp')
    try:
        pending.write_text(json.dumps(value), encoding='utf-8')
        pending.replace(path)
    except OSError:
        pass


class StudioCapture:
    def __init__(self, root):
        self.root = Path(root)
        self.runtime_id = uuid.uuid4().hex
        self.last_id = 0
        self.last_poll = self.last_publish = 0.
        self.writer = self.video = self.labels = self.pupil = self.guided = None
        self.kind = None
        self.state = {'phase': 'idle'}
        self.prefix = None
        self.paused = False
        self.last_ui = 0.
        self.last_video = 0.
        self.last_hash = None
        self.reload_requested = True

    def command(self, command, now):
        if command.get('runtimeId') != self.runtime_id:
            return
        if type(command.get('id')) is not int or command['id'] <= self.last_id:
            return
        self.last_id = command['id']
        self.last_ui = now
        action = command.get('action')
        if action == 'begin':
            if self.writer is not None or self.pupil is not None:
                raise ValueError('A calibration is already recording')
            kind = command.get('kind')
            if kind not in ('pupils', 'benchmark', 'benchmark-quick', 'enroll'):
                raise ValueError('Unknown calibration')
            self.kind = kind
            self.prefix = self.root / 'captures' / (kind+'-'+time.strftime('%Y%m%d-%H%M%S')+'-'+uuid.uuid4().hex[:6])
            self.prefix.parent.mkdir(parents=True, exist_ok=True)
            self.paused = False
            self.state = {'phase': 'recording', 'prefix': str(self.prefix), 'kind': kind}
            if kind in ('benchmark', 'benchmark-quick', 'enroll'):
                self.writer = CaptureWriter(self.prefix.with_suffix('.qpcap'))
                self.labels = self.prefix.with_suffix('.qplabel.jsonl').open('x', encoding='utf-8')
                if kind.startswith('benchmark'):
                    seed = uuid.uuid4().int & 0xFFFFFFFF
                    self.guided = GuidedSession(self.prefix.with_suffix('.qpsession.json'), benchmark_steps(seed, quick=kind == 'benchmark-quick'), 'benchmark-v1',
                                                time.monotonic_ns(), seed=seed, recordEvery=2)
                else:
                    pace = SLOW if command.get('slow') is True else 1.
                    self.guided = EnrollmentSession(self.prefix.with_suffix('.qpsession.json'), enrollment_steps(pace), time.monotonic_ns(), pace=pace)
                self.frames = 0
                self.last_record = 0.
                self.last_hash = None
                self.label_sequence = self.label_schema = None
                self.open_video()
            else:
                from pupil_dilation import PupilDilation
                self.pupil = PupilDilation(str(self.prefix)+'.pupils.json', enabled=False)
                self.pupil.calibrate()
        elif action == 'cancel':
            self.finish(False)
        elif action == 'pause':
            self.paused = not self.paused
        elif action == 'reload':
            self.reload_requested = True
        elif action == 'skip' and self.guided:
            self.guided.skip(time.monotonic_ns())
            if self.guided.completed:
                self.finish(True)

    def update(self, strip, header, payload, monotonic_ns, labels):
        now = monotonic_ns / 1e9
        if now-self.last_poll >= .1:
            self.last_poll = now
            try:
                command = json.loads((self.root/'studio.command.json').read_text(encoding='utf-8-sig'))
            except (OSError, ValueError, KeyError, TypeError) as error:
                command = None
            if isinstance(command, dict):
                try:
                    self.command(command, now)
                except (OSError, ValueError, KeyError, TypeError) as error:
                    self.finish(False)
                    self.state['error'] = str(error)
        try:
            if self.pupil:
                if now-self.last_ui > 3:
                    self.finish(False)
                    self.state['error'] = 'Calibration stopped because the Studio window stopped responding. Your previous calibration is still in use.'
                else:
                    self.pupil.update(strip)
                    from pupil_dilation import DURATION, STAGES
                    phase = self.pupil.phase
                    elapsed = min(DURATION, max(0, now-self.pupil.started))
                    self.state.update(level=self.pupil.level, remaining=round(DURATION-elapsed), progress=elapsed/DURATION,
                        index=phase or 0, total=len(STAGES), title=STAGES[phase or 0], instruction=self.pupil.message)
                    if self.pupil.result in ('passed', 'failed'):
                        self.state['error'] = '' if self.pupil.result == 'passed' else self.pupil.message
                        self.finish(self.pupil.result == 'passed')
            if self.guided:
                self.update_guided(strip, header, payload, monotonic_ns, labels, now)
        except (OSError, ValueError, cv2.error) as error:
            self.finish(False)
            self.state['error'] = str(error)
        if now-self.last_publish >= .2:
            self.last_publish = now
            publish(self.root/'studio.state.json', dict(self.state, runtimeId=self.runtime_id, ack=self.last_id, time=time.time()))

    def open_video(self):
        self.video = cv2.VideoWriter(str(self.prefix)+'.avi', cv2.VideoWriter_fourcc(*'MJPG'), 8., (1000, 200), True)
        if not self.video.isOpened():
            raise OSError('Couldn’t save the recording. Make sure this PC has free disk space, then try again.')

    def update_guided(self, strip, header, payload, monotonic_ns, labels, now):
        if strip.shape != (400, 2000):
            raise ValueError('This needs all five headset cameras. Restart tracking, then try again.')
        if now-self.last_ui > 3:
            self.paused = True
        guided = self.guided
        guided.update(monotonic_ns, self.paused)
        if guided.completed:
            self.finish(True)
            return
        if not self.paused:
            self.frames += 1
            every, hz = guided.record.get('recordEvery'), guided.current.record_hz
            holding = guided.elapsed >= guided.current.ramp
            if (every and self.frames % every == 0) or (hz and holding and now-self.last_record >= 1/hz - .01):
                if not every:
                    guided.note_frame(self.writer.frame_count, monotonic_ns)
                self.writer.write(header, payload, monotonic_ns, time.time_ns())
                self.last_record = now
            label = labels.nearest_sample(monotonic_ns) if labels else None
            if isinstance(guided, EnrollmentSession):
                fingerprint = zlib.crc32(payload)
                fresh = label is not None and abs(int(label['arrivalMonotonicNs'])-monotonic_ns) <= 100_000_000
                guided.observe(strip, dict(zip(labels.schema_names or [], label['values'])) if fresh else None,
                               fingerprint == self.last_hash)
                self.last_hash = fingerprint
            if label is not None and label.get('sourceSequence') != self.label_sequence:
                if labels.schema_names != self.label_schema:
                    self.label_schema = list(labels.schema_names or [])
                    self.labels.write(json.dumps({'type': 'schema', 'names': self.label_schema})+'\n')
                self.labels.write(json.dumps(label)+'\n')
                self.label_sequence = label.get('sourceSequence')
            if now-self.last_video >= .125:
                self.video.write(cv2.cvtColor(cv2.resize(strip, (1000, 200)), cv2.COLOR_GRAY2BGR))
                self.last_video = now
        self.state.update(guided.ui(), paused=self.paused, automatic=True, ready=True, count=0)
        if self.paused:
            self.state['cue'] = 'Paused'

    def finish(self, complete):
        if self.guided:
            self.guided.finish(complete)
        for obj in (self.writer, self.labels):
            if obj is not None:
                if isinstance(obj, CaptureWriter): obj.close(completed=complete)
                else: obj.close()
        if self.video is not None: self.video.release()
        if self.pupil is not None: self.pupil.close()
        self.writer = self.labels = self.video = self.pupil = self.guided = None
        self.state.update(phase='complete' if complete else 'cancelled', completed=complete)

    def close(self):
        if self.writer is not None or self.pupil is not None:
            self.finish(False)
