"""Calibration on the existing camera stream; never starts a headset service."""
from dataclasses import replace
import json
import math
from pathlib import Path
import time
import uuid
import zlib
import cv2
from capture_format import CaptureWriter, TRANSPORT_HEADER
from tongue_still_capture import TongueStillCaptureSession, TONGUE_REFINEMENT_PROMPTS
from extra_face_capture import ExtraFaceCapture, FAMILIES
from guided_session import EnrollmentSession, GuidedSession, benchmark_steps, enrollment_steps


def publish(path, value):
    """A busy status reader must not tear down the headset session."""
    pending = path.with_suffix('.tmp')
    try:
        pending.write_text(json.dumps(value), encoding='utf-8')
        pending.replace(path)
    except OSError:
        pass


RELAX = .75


def puff_amount(targets):
    level = max(targets.values(), default=0.)
    return 'Relaxed' if level == 0 else 'Halfway' if level < 1 else 'Full'


class StudioCapture:
    def __init__(self, root):
        self.root = Path(root)
        self.runtime_id = uuid.uuid4().hex
        self.last_id = 0
        self.last_poll = self.last_publish = 0.
        self.session = self.writer = self.video = self.labels = self.timeline = self.pupil = self.guided = None
        self.kind = None
        self.state = {'phase': 'idle'}
        self.prefix = None
        self.paused = False
        self.last_ui = 0.
        self.changed = time.monotonic()
        self.last_sample = self.last_video = 0.
        self.last_hash = None
        self.reloading = False
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
            if kind not in ('tongue', 'pupils', 'benchmark', 'benchmark-quick', 'enroll', *FAMILIES):
                raise ValueError('Unknown calibration')
            self.kind = kind
            self.automatic = command.get('automatic', True) is True
            self.settle = min(5., max(2., float(command.get('settle', 2.5))))
            self.prefix = self.root / 'captures' / (kind+'-'+time.strftime('%Y%m%d-%H%M%S')+'-'+uuid.uuid4().hex[:6])
            self.prefix.parent.mkdir(parents=True, exist_ok=True)
            self.paused = False
            self.changed = now
            self.state = {'phase': 'recording', 'prefix': str(self.prefix), 'kind': kind}
            if kind in ('benchmark', 'benchmark-quick', 'enroll'):
                self.writer = CaptureWriter(self.prefix.with_suffix('.qpcap'))
                self.labels = self.prefix.with_suffix('.qplabel.jsonl').open('x', encoding='utf-8')
                if kind.startswith('benchmark'):
                    seed = uuid.uuid4().int & 0xFFFFFFFF
                    self.guided = GuidedSession(self.prefix.with_suffix('.qpsession.json'), benchmark_steps(seed, quick=kind == 'benchmark-quick'), 'benchmark-v1',
                                                time.monotonic_ns(), seed=seed, recordEvery=2)
                else:
                    self.guided = EnrollmentSession(self.prefix.with_suffix('.qpsession.json'), enrollment_steps(), time.monotonic_ns())
                self.frames = 0
                self.last_record = 0.
                self.last_hash = None
                self.label_sequence = self.label_schema = None
                self.open_video()
            elif kind == 'pupils':
                from pupil_dilation import PupilDilation
                self.pupil = PupilDilation(str(self.prefix)+'.pupils.json', enabled=False)
                self.pupil.handle_key('c')
            else:
                self.writer = CaptureWriter(self.prefix.with_suffix('.qpcap'))
                if kind == 'tongue':
                    prompts = [replace(p, context=p.context+f'/repetition-{r}', recommended_captures=4, minimum_captures=4)
                               for r in range(3) for p in TONGUE_REFINEMENT_PROMPTS]
                    self.session = TongueStillCaptureSession(self.prefix.with_suffix('.qpsession.json'), prompts=prompts,
                        session_type='tongue-stereo-refinement-v2', capture_policy='guided holds; 2 Hz samples; third complete round held out')
                    self.labels = self.prefix.with_suffix('.qplabel.jsonl').open('x', encoding='utf-8')
                else:
                    self.session = ExtraFaceCapture(self.prefix.with_suffix('.qpsession.json'), kind)
                self.open_video()
                self.last_hash = None
        elif action == 'cancel':
            self.finish(False)
        elif action == 'pause':
            self.paused = not self.paused
            self.changed = now
        elif action == 'reload':
            self.reload_requested = True
        elif action == 'skip' and self.guided:
            self.guided.skip(time.monotonic_ns())
            if self.guided.completed:
                self.finish(True)
        elif action == 'skip' and self.kind in FAMILIES and self.session:
            self.session.handle_key('k')
            self.changed = now
            if self.session.completed:
                self.finish(True)
        elif action in ('capture', 'next', 'undo') and self.session and not self.automatic:
            self.session.handle_key({'capture': ' ', 'next': '\r', 'undo': 'x'}[action])
            if action == 'next':
                self.changed = now
            if self.session.completed:
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
            if self.session:
                if strip.shape != (400, 2000):
                    raise ValueError('Calibration needs all five headset cameras. Restart tracking, then try again.')
                if now-self.last_ui > 3:
                    self.paused = True
                    self.changed = now
                session = self.session
                elapsed = now-self.changed
                face = self.kind in FAMILIES
                reform = face and self.automatic
                attempts = 3 if face else 4
                relaxing = reform and elapsed < RELAX
                lead = self.settle + (RELAX if reform else 0.)
                ready = not self.paused and elapsed >= lead
                label = labels.nearest_sample(monotonic_ns) if labels else None
                label_ready = self.kind != 'tongue' or (label is not None and labels.schema_names
                    and abs(label['arrivalMonotonicNs']-monotonic_ns) <= 35_000_000)
                count = len(session.active_samples())
                if self.automatic and ready and label_ready and now-self.last_sample >= .5 and count < attempts:
                    fingerprint = zlib.crc32(payload)
                    if fingerprint != self.last_hash:
                        session.handle_key(' ')
                        self.last_hash = fingerprint
                if session.pending_capture and label_ready and not self.paused:
                    h, p = header, payload
                    if self.kind == 'tongue':
                        p = strip[:, 800:].tobytes()
                        parts = list(TRANSPORT_HEADER.unpack(header)); parts[5] = parts[7] = 1200; parts[9] = len(p); parts[10] = 0x1c
                        h = TRANSPORT_HEADER.pack(*parts)
                    if session.consume_frame(self.writer, h, p, monotonic_ns, time.time_ns()):
                        sample = session.samples[-1]
                        per_round = len(session.prompts) // 3
                        sample['repetition'] = session.current_index // per_round
                        sample['poseIndex'] = session.current_index % per_round
                        session._save()
                        self.last_sample = now
                        if reform and len(session.active_samples()) < attempts:
                            self.changed = now
                        if self.labels:
                            self.labels.write(json.dumps({'type':'schema', 'names': labels.schema_names})+'\n')
                            self.labels.write(json.dumps(label)+'\n'); self.labels.flush()
                if not self.paused and now-self.last_video >= .125:
                    self.video.write(cv2.cvtColor(cv2.resize(strip, (1000,200)), cv2.COLOR_GRAY2BGR))
                    self.timeline.write(json.dumps({'frame':self.video_index, 'monotonicNs':monotonic_ns,
                        'promptIndex':session.current_index, 'settling':not ready, 'targets':session.current.targets})+'\n')
                    self.video_index += 1; self.last_video = now
                count = len(session.active_samples())
                remaining = max(0., lead-elapsed)
                forming = 0. if relaxing else 1. if ready or not reform else min(1., (elapsed-RELAX)/self.settle)
                amount = puff_amount(session.current.targets) if face else None
                self.state.update(title=session.current.name.split(': ', 1)[-1] if face else session.current.name, instruction=session.current.instruction.split(' Skip with K', 1)[0],
                    targets={k: v*forming for k, v in session.current.targets.items()}, index=session.current_index, total=len(session.prompts), count=count,
                    paused=self.paused, automatic=self.automatic, ready=bool(label_ready),
                    cue='Paused' if self.paused else 'Waiting for face tracking…' if not label_ready
                        else ('Let the air out' if self.kind == 'puff' else 'Relax') if relaxing
                        else ('Hold ' + amount.lower() if amount else 'Hold') if ready or not self.automatic
                        else (amount + ' · ' if amount else '') + str(math.ceil(remaining)),
                    progress=(session.current_index+min(1, count/attempts))/len(session.prompts),
                    remaining=remaining)
                if self.automatic and not self.paused and count >= attempts and now-self.last_sample >= .5:
                    session.handle_key('\r'); self.changed = now
                    if session.completed:
                        self.finish(True)
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
        self.timeline = self.prefix.with_suffix('.video.jsonl').open('x', encoding='utf-8')
        self.video_index = 0

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
                self.timeline.write(json.dumps({'frame': self.video_index, 'monotonicNs': monotonic_ns, 'promptIndex': guided.index,
                    'condition': guided.current.condition})+'\n')
                self.video_index += 1; self.last_video = now
        self.state.update(guided.ui(), paused=self.paused, automatic=True, ready=True, count=0)
        if self.paused:
            self.state['cue'] = 'Paused'

    def finish(self, complete):
        if self.guided:
            self.guided.finish(complete)
        if self.session:
            self.session.finish(completed=complete)
            path = self.session.path
            data = json.loads(path.read_text(encoding='utf-8')); data['guidedRecording'] = True
            publish(path, data)
        for obj in (self.writer, self.labels, self.timeline):
            if obj is not None:
                if isinstance(obj, CaptureWriter): obj.close(completed=complete)
                else: obj.close()
        if self.video is not None: self.video.release()
        if self.pupil is not None: self.pupil.close()
        self.session = self.writer = self.labels = self.timeline = self.video = self.pupil = self.guided = None
        self.state.update(phase='complete' if complete else 'cancelled', completed=complete)

    def close(self):
        if self.writer is not None or self.pupil is not None:
            self.finish(False)
