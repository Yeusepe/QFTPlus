"""Calibration on the existing camera stream; never starts a headset service."""
from dataclasses import replace
import json
from pathlib import Path
import time
import uuid
import zlib
import cv2
from capture_format import CaptureWriter, TRANSPORT_HEADER
from tongue_still_capture import TongueStillCaptureSession, TONGUE_REFINEMENT_PROMPTS
from extra_face_capture import ExtraFaceCapture


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
        self.session = self.writer = self.video = self.labels = self.timeline = self.pupil = None
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
            if kind not in ('tongue', 'puff', 'pupils'):
                raise ValueError('Unknown calibration')
            self.kind = kind
            self.automatic = command.get('automatic', True) is True
            self.settle = min(5., max(2., float(command.get('settle', 2.5))))
            self.prefix = self.root / 'captures' / (kind+'-'+time.strftime('%Y%m%d-%H%M%S')+'-'+uuid.uuid4().hex[:6])
            self.prefix.parent.mkdir(parents=True, exist_ok=True)
            self.paused = False
            self.changed = now
            self.state = {'phase': 'recording', 'prefix': str(self.prefix), 'kind': kind}
            if kind == 'pupils':
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
                    self.session = ExtraFaceCapture(self.prefix.with_suffix('.qpsession.json'), 'puff')
                self.video = cv2.VideoWriter(str(self.prefix)+'.avi', cv2.VideoWriter_fourcc(*'MJPG'), 8., (1000, 200), True)
                if not self.video.isOpened():
                    raise OSError('Could not open the local recording file')
                self.timeline = self.prefix.with_suffix('.video.jsonl').open('x', encoding='utf-8')
                self.video_index = 0
                self.last_hash = None
        elif action == 'cancel':
            self.finish(False)
        elif action == 'pause':
            self.paused = not self.paused
            self.changed = now
        elif action == 'reload':
            self.reload_requested = True
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
                    self.state['error'] = 'Calibration window disconnected. Your previous profile is unchanged.'
                else:
                    self.pupil.update(strip)
                    from pupil_dilation import PHASES
                    phase = self.pupil.phase
                    self.state.update(bright=phase is not None and phase % 2 == 0,
                        remaining=max(0, PHASES[phase][1]-(now-self.pupil.started)) if phase is not None else 0,
                        index=phase or 0, total=4, title='Look at the center cross', instruction='Keep your head still. Blink normally.')
                    if self.pupil.result in ('passed', 'failed'):
                        self.state['error'] = '' if self.pupil.result == 'passed' else self.pupil.message
                        self.finish(self.pupil.result == 'passed')
            if self.session:
                if strip.shape != (400, 2000):
                    raise ValueError('Connect all five cameras before calibrating')
                if now-self.last_ui > 3:
                    self.paused = True
                    self.changed = now
                session = self.session
                elapsed = now-self.changed
                ready = not self.paused and elapsed >= self.settle
                label = labels.nearest_sample(monotonic_ns) if labels else None
                label_ready = self.kind != 'tongue' or (label is not None and labels.schema_names
                    and abs(label['arrivalMonotonicNs']-monotonic_ns) <= 35_000_000)
                count = len(session.active_samples())
                if self.automatic and ready and label_ready and now-self.last_sample >= .5 and count < 4:
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
                        sample['repetition'] = session.current_index // (14 if self.kind == 'tongue' else 7)
                        sample['poseIndex'] = session.current_index % (14 if self.kind == 'tongue' else 7)
                        session._save()
                        self.last_sample = now
                        if self.labels:
                            self.labels.write(json.dumps({'type':'schema', 'names': labels.schema_names})+'\n')
                            self.labels.write(json.dumps(label)+'\n'); self.labels.flush()
                if not self.paused and now-self.last_video >= .125:
                    self.video.write(cv2.cvtColor(cv2.resize(strip, (1000,200)), cv2.COLOR_GRAY2BGR))
                    self.timeline.write(json.dumps({'frame':self.video_index, 'monotonicNs':monotonic_ns,
                        'promptIndex':session.current_index, 'settling':not ready, 'targets':session.current.targets})+'\n')
                    self.video_index += 1; self.last_video = now
                count = len(session.active_samples())
                self.state.update(title=session.current.name.split(': ', 1)[-1] if self.kind == 'puff' else session.current.name, instruction=session.current.instruction.replace(' Skip with K if you cannot isolate this pose.', ''),
                    targets=session.current.targets, index=session.current_index, total=len(session.prompts), count=count,
                    paused=self.paused, automatic=self.automatic, ready=bool(label_ready),
                    cue='Paused' if self.paused else 'Waiting for face tracking…' if not label_ready else 'Relax, then pose' if not ready else 'Hold',
                    progress=(session.current_index+min(1, count/4))/len(session.prompts),
                    remaining=max(0, self.settle-elapsed))
                if self.automatic and not self.paused and count >= 4 and now-self.last_sample >= .5:
                    session.handle_key('\r'); self.changed = now
                    if session.completed:
                        self.finish(True)
        except (OSError, ValueError, cv2.error) as error:
            self.finish(False)
            self.state['error'] = str(error)
        if now-self.last_publish >= .2:
            self.last_publish = now
            publish(self.root/'studio.state.json', dict(self.state, runtimeId=self.runtime_id, ack=self.last_id, time=time.time()))

    def finish(self, complete):
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
        self.session = self.writer = self.labels = self.timeline = self.video = self.pupil = None
        self.state.update(phase='complete' if complete else 'cancelled', completed=complete)

    def close(self):
        if self.writer is not None or self.pupil is not None:
            self.finish(False)
