"""Calibration on the existing camera stream; never starts a headset service."""
import json
from contextlib import ExitStack
from pathlib import Path
import time
import uuid
import zlib
import cv2
from capture_format import CaptureWriter
from guided_session import SLOW, EnrollmentSession, GuidedSession, benchmark_steps, enrollment_steps
from label_capture import fresh_values
from pupil_dilation import DURATION, STAGES, PupilDilation


def publish(path, value):
    """A busy status reader must not tear down the headset session."""
    pending = path.with_suffix('.tmp')
    try:
        pending.write_text(json.dumps(value), encoding='utf-8')
        pending.replace(path)
    except OSError:
        pass


class StudioCapture:
    def __init__(self, root, labels=None):
        self.root = Path(root)
        self.native_labels = labels
        self.last_frame_ns = None
        self.last_stream_ns = None
        self.waiting_for_face = False
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

    def face_issue(self, now_ns):
        labels = self.native_labels
        sample = labels.latest if labels else None
        native = fresh_values(sample, labels.schema_names, now_ns) if labels else None
        if (native is not None and sample.get('faceFlags', 0) & 1
                and 'JawDrop' in native and any(n.startswith('LipPucker') for n in native)
                and ('TongueOut' in native or 'TongueTipInterdental' in native)):
            return ''
        status = labels.status if labels else None
        if (status and 0 <= now_ns - status['arrivalMonotonicNs'] < 3_000_000_000
                and status['steamLinkPortConflict']):
            return 'CalibrationFacePortConflict'
        return 'CalibrationFaceDataUnavailable'

    def data_issue(self, now_ns):
        issue = self.face_issue(now_ns)
        if issue:
            return issue
        camera_ns = self.last_frame_ns if self.guided else self.last_stream_ns
        if camera_ns is None or not 0 <= now_ns - camera_ns <= 500_000_000:
            return 'CalibrationCameraDataUnavailable'
        return ''

    def wait_for_data(self, now_ns, issue):
        self.guided.update(now_ns, True)
        self.waiting_for_face = True
        self.state.update(self.guided.ui(), paused=self.paused, waitingForFace=True, cue='GuidePaused', instruction=issue)

    def camera_frame(self, now_ns, *, full=True):
        self.last_stream_ns = now_ns
        if not full:
            return
        if self.guided and self.last_frame_ns is not None and now_ns - self.last_frame_ns > 500_000_000:
            self.wait_for_data(now_ns, 'CalibrationCameraDataUnavailable')
        self.last_frame_ns = now_ns

    def command(self, command, now):
        if command.get('runtimeId') != self.runtime_id:
            return
        if type(command.get('id')) is not int or command['id'] <= self.last_id:
            return
        self.last_id = command['id']
        self.last_ui = now
        action = command.get('action')
        if action in ('skip', 'repeat') and self.guided and 'index' in command:
            if type(command['index']) is not int or command['index'] != self.guided.index:
                return
        if action == 'begin':
            if self.writer is not None or self.pupil is not None:
                raise ValueError("GuideACalibrationIsAlreadyRecording")
            kind = command.get('kind')
            if kind not in ('pupils', 'benchmark', 'benchmark-quick', 'benchmark-unlabeled', 'enroll'):
                raise ValueError("GuideUnknownCalibration")
            self.kind = kind
            self.state['kind'] = kind
            if kind == 'benchmark-unlabeled':
                if not self.camera_ready(int(now * 1e9)):
                    raise ValueError("CalibrationCameraDataUnavailable")
            elif kind != 'pupils' and (issue := self.data_issue(int(now * 1e9))):
                raise ValueError(issue)
            self.prefix = self.root / 'captures' / (kind+'-'+time.strftime('%Y%m%d-%H%M%S')+'-'+uuid.uuid4().hex[:6])
            self.prefix.parent.mkdir(parents=True, exist_ok=True)
            self.paused = False
            self.waiting_for_face = False
            self.state = {'phase': 'recording', 'prefix': str(self.prefix), 'kind': kind}
            if kind != 'pupils':
                self.writer = CaptureWriter(self.prefix.with_suffix('.qpcap'))
                self.open_video()
            if kind == 'benchmark-unlabeled':
                self.started = now
                self.preview_slot = -1
            elif kind in ('benchmark', 'benchmark-quick', 'enroll'):
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
            else:
                self.pupil = PupilDilation(str(self.prefix)+'.pupils.json', enabled=False, asynchronous=True)
                self.pupil.calibrate(command.get('sourceEyes', [0, 1]))
        elif action == 'finish' and self.kind == 'benchmark-unlabeled' and self.writer:
            if not self.writer.frame_count:
                raise ValueError("BenchmarkNoCameraFrames")
            self.finish(True)
        elif action == 'cancel':
            self.finish(False)
        elif action == 'pause':
            self.paused = not self.paused
            if self.pupil:
                self.pupil.hold(self.paused, now)
            elif not self.paused and isinstance(self.guided, EnrollmentSession):
                self.guided.repeat(int(now*1e9))
        elif action == 'reload':
            self.reload_requested = True
        elif action == 'repeat' and self.guided:
            if not self.data_issue(int(now * 1e9)):
                self.guided.repeat(time.monotonic_ns())
        elif action == 'skip' and self.guided:
            if self.data_issue(int(now * 1e9)):
                return
            if isinstance(self.guided, EnrollmentSession) and self.guided.current.slot == 'neutral':
                return
            self.guided.skip(time.monotonic_ns())
            if self.guided.completed:
                self.finish(True)

    def camera_ready(self, now_ns):
        return self.last_stream_ns is not None and 0 <= now_ns - self.last_stream_ns <= 500_000_000

    def poll(self, monotonic_ns):
        now = monotonic_ns / 1e9
        if now-self.last_poll >= .1:
            self.last_poll = now
            try:
                command = json.loads((self.root/'studio.command.json').read_text(encoding='utf-8-sig'))
            except (OSError, ValueError, KeyError, TypeError):
                command = None
            if isinstance(command, dict):
                try:
                    self.command(command, now)
                except (OSError, ValueError, KeyError, TypeError, cv2.error) as error:
                    self.finish(False)
                    self.state['error'] = str(error)
        issue = self.data_issue(monotonic_ns)
        self.state.update(faceReady=not issue, faceIssue=issue, cameraReady=self.camera_ready(monotonic_ns))
        if self.kind == 'benchmark-unlabeled' and self.writer:
            self.state['elapsed'] = int(max(0, now - self.started))
            if now - self.last_ui > 15:
                self.finish(False)
                self.state['error'] = 'BenchmarkRecordingInterrupted'
        if self.kind == 'benchmark-unlabeled' and self.state['cameraReady'] and self.state.get('error') == 'CalibrationCameraDataUnavailable':
            self.state.pop('error', None)
        elif not issue and self.state.get('error') in ('CalibrationFacePortConflict', 'CalibrationFaceDataUnavailable', 'CalibrationCameraDataUnavailable'):
            self.state.pop('error', None)
        if self.guided and issue:
            self.wait_for_data(monotonic_ns, issue)
        if now-self.last_publish >= .2:
            self.last_publish = now
            publish(self.root/'studio.state.json', dict(self.state, runtimeId=self.runtime_id, ack=self.last_id, time=time.time()))

    def update(self, strip, header, payload, monotonic_ns, labels, *, headset_pupils=False):
        self.native_labels = labels
        self.camera_frame(monotonic_ns)
        self.poll(monotonic_ns)
        now = monotonic_ns / 1e9
        try:
            if self.pupil and not headset_pupils:
                self.pupil.update(strip)
                self.pupil_state(now)
            if self.guided:
                self.update_guided(strip, header, payload, monotonic_ns, labels, now)
            elif self.kind == 'benchmark-unlabeled' and self.writer:
                self.writer.write(header, payload, monotonic_ns, time.time_ns())
                slot = int((now - self.started) * 8)
                if slot > self.preview_slot:
                    self.video.write(cv2.cvtColor(cv2.resize(strip, (1000, 200)), cv2.COLOR_GRAY2BGR))
                    self.preview_slot = slot
        except (OSError, ValueError, cv2.error) as error:
            self.finish(False)
            self.state['error'] = str(error)

    def update_pupils(self, measurements, monotonic_ns):
        if self.pupil is not None:
            now = monotonic_ns / 1e9
            self.pupil.update_measurements(measurements, now=now)
            self.pupil_state(now)

    def pupil_state(self, now):
        if now-self.last_ui > 3:
            self.finish(False)
            self.state['error'] = "GuideCalibrationStoppedBecauseTheStudioWindowStoppedRespondingYourPrevious"
            return
        phase = self.pupil.phase
        elapsed = min(DURATION, self.pupil.paused_elapsed if self.paused else max(0, now-self.pupil.started))
        self.state.update(level=self.pupil.level, remaining=round(DURATION-elapsed), progress=elapsed/DURATION,
            index=phase or 0, total=len(STAGES), title=STAGES[phase or 0], instruction=self.pupil.message)
        self.state['paused'] = self.paused
        if self.paused:
            self.state.update(level=0., instruction='GuidePupilPausedRestart')
        if self.pupil.result in ('passed', 'failed'):
            self.state['error'] = '' if self.pupil.result == 'passed' else self.pupil.message
            self.finish(self.pupil.result == 'passed')

    def open_video(self):
        self.video = cv2.VideoWriter(str(self.prefix)+'.avi', cv2.VideoWriter_fourcc(*'MJPG'), 8., (1000, 200), True)
        if not self.video.isOpened():
            raise OSError("GuideCouldnTSaveTheRecordingMakeSureThisPCHas")

    def update_guided(self, strip, header, payload, monotonic_ns, labels, now):
        if now-self.last_ui > 3:
            self.paused = True
        guided = self.guided
        issue = self.data_issue(monotonic_ns)
        if issue:
            self.wait_for_data(monotonic_ns, issue)
            return
        if self.waiting_for_face:
            if isinstance(guided, EnrollmentSession):
                guided.repeat(monotonic_ns)
            self.waiting_for_face = False
        self.state['waitingForFace'] = False
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
            label = labels.latest if labels else None
            if isinstance(guided, EnrollmentSession):
                fingerprint = zlib.crc32(payload)
                guided.observe(strip, fresh_values(label, labels.schema_names, monotonic_ns), fingerprint == self.last_hash)
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
            self.state['cue'] = "GuidePaused"

    def finish(self, complete):
        try:
            with ExitStack() as closing:
                if self.pupil is not None: closing.callback(self.pupil.close)
                if self.video is not None: closing.callback(self.video.release)
                if self.labels is not None: closing.callback(self.labels.close)
                if self.writer is not None: closing.callback(self.writer.close, completed=complete)
                if self.guided: self.guided.finish(complete)
        except (OSError, cv2.error):
            complete = False
            self.state['error'] = 'GuideCouldnTSaveTheRecordingMakeSureThisPCHas'
        finally:
            self.writer = self.labels = self.video = self.pupil = self.guided = None
        self.state.update(phase='complete' if complete else 'cancelled', completed=complete)

    def close(self):
        if self.writer is not None or self.pupil is not None:
            self.finish(False)
