package com.qftplus.headset;
import java.io.*;
import java.nio.*;
import java.nio.file.*;
import java.util.*;

final class Calibration {
    static final int FEATURES = 999 + 512;
    static final int[] PROMPTS = {R.string.relax_and_look_ahead, R.string.open_your_mouth_wide, R.string.push_your_lips_forward_into_a_kiss, R.string.puff_both_cheeks, R.string.puff_only_your_left_cheek, R.string.puff_only_your_right_cheek, R.string.stick_your_tongue_straight_out, R.string.point_your_tongue_up, R.string.point_your_tongue_down, R.string.point_your_tongue_left, R.string.point_your_tongue_right, R.string.suck_in_both_cheeks};
    private final boolean pupils;
    private final int redoStage;
    private boolean stagesLoaded;
    private long start;
    private long lastFeature, heartbeat = System.nanoTime();
    private long lastSample, heldSince;
    private int faceStep, pupilRound, retries;
    private boolean holdVisible;
    private boolean validating;
    private int pupilSource = 3;
    private int pace = 1;
    private int extensions;
    private int feedbackReason;
    private final int[] faceFailures = new int[12];
    private float peak;
    private String assessment = "";
    private final ArrayList<ArrayList<float[]>> holds = new ArrayList<>();
    private final ArrayList<ArrayList<float[]>> checks = new ArrayList<>();
    private float[] previousFeatures;
    private final ArrayList<double[]> pupilSamples = new ArrayList<>();
    volatile boolean done;
    volatile String message = "";
    private boolean failed;
    private boolean saved;
    private boolean presented;
    private String waiting;
    private final float[] previousDiameter = new float[2];
    private final long[] previousTime = new long[2];
    private final android.content.Context context;
    private String getString(int id, Object... args) { return context.getString(id, args); }
    Calibration(android.content.Context context, boolean pupils) { this(context, pupils, -1); }
    Calibration(android.content.Context context, boolean pupils, int redoStage) {
        this.context = context;
        this.pupils = pupils;
        this.redoStage = redoStage;
        faceStep = Math.max(0, redoStage);
        waiting = getString(R.string.waiting_for_eye_and_face_cameras);
        for (int i = 0; i < FaceCalibrationStages.COUNT; i++) {
            holds.add(new ArrayList<>());
            checks.add(new ArrayList<>());
        }
    }
    boolean isPupils() { return pupils; }
    boolean isStageRedo() { return redoStage >= 0; }
    synchronized boolean loadStages(File directory) {
        if (!isStageRedo() || stagesLoaded) return true;
        ByteBuffer capture = FaceCalibrationStages.read(directory);
        if (capture == null) return false;
        capture.position(Long.BYTES);
        int[] counts = new int[2 * FaceCalibrationStages.COUNT];
        for (int i = 0; i < counts.length; i++) counts[i] = capture.getInt();
        for (int i = 0; i < counts.length; i++) {
            int stage = i % FaceCalibrationStages.COUNT;
            ArrayList<float[]> frames = (i < FaceCalibrationStages.COUNT ? holds : checks).get(stage);
            if (stage == redoStage) {
                capture.position(capture.position() + counts[i] * FEATURES * Float.BYTES);
                continue;
            }
            for (int j = 0; j < counts[i]; j++) {
                float[] frame = new float[FEATURES];
                for (int k = 0; k < FEATURES; k++) {
                    frame[k] = capture.getFloat();
                    if (!Float.isFinite(frame[k])) return false;
                }
                frames.add(frame);
            }
        }
        stagesLoaded = true;
        return true;
    }
    synchronized void source(int mask) { if (start == 0 && mask >= 1 && mask <= 3) pupilSource = mask; }
    synchronized void pace(boolean slow) { if(start==0)pace=slow?2:1; }
    synchronized boolean validating() { return validating; }
    synchronized boolean waitingForSamples() { return extensions>0; }
    synchronized String feedback() { return feedbackReason == 0 ? "" : getString(feedbackReason); }
    synchronized boolean held() { return heldSince != 0; }
    synchronized void presented() { presented = true; }
    synchronized void heartbeat() { heartbeat = System.nanoTime(); }
    synchronized double elapsed() {
        double t = start == 0 ? 0 : ((heldSince != 0 ? heldSince : System.nanoTime()) - start) / 1e9;
        return pupils ? Math.min(t,55+pupilRound*25) : Math.min(t/pace, faceStep*6+5.999);
    }
    synchronized boolean started() { return start != 0; }
    synchronized String assessment() { return assessment; }
    synchronized boolean retrying() { return retries > 0; }
    static double stimulus(double t) {
        if (t < 30) return 0;
        t = (t-30)%25;
        if (t < 5) return .5-.5*Math.cos(Math.PI*t/5);
        if (t < 10) return 1;
        if (t < 15) return .5+.5*Math.cos(Math.PI*(t-10)/5);
        return 0;
    }
    synchronized String prompt() {
        heartbeat();
        if (!done && start != 0 && heldSince == 0 && System.nanoTime()-lastSample > 3000000000L)
            fail(getString(R.string.tracking_stopped_check_the_headset_fit_and_try));
        if (done) return message;
        if (start == 0) return waiting;
        double t = elapsed();
        if (pupils) return t < 30 ? getString(R.string.look_at_the_circle_let_your_eyes_adjust) : getString(retries > 0 ? R.string.pupil_prompt_retry : R.string.pupil_prompt, pupilRound+1);
        int step = Math.min(11, (int)t/6);
        return getString(validating ? R.string.face_prompt_validation : retries > 0 ? R.string.face_prompt_retry : R.string.face_prompt, getString(PROMPTS[step]), getString(t%6 < 2 ? R.string.get_ready : t%6 < 5 ? R.string.hold_still : R.string.relax));
    }
    synchronized void fail(String reason) { if (!saved) { failed = true; done = true; message = reason; } }
    synchronized void saved(String result) { saved = true; done = true; message = result; }
    synchronized boolean cancelled() { return failed; }
    synchronized void hold(boolean held, long now) {
        if (done || held == (heldSince != 0)) return;
        if (held) { heldSince = now; return; }
        heldSince = 0; lastSample = heartbeat = now;
        if (start == 0) return;
        if (pupils) {
            pupilSamples.clear(); pupilRound = retries = 0;
            start = now;
            Arrays.fill(previousTime,0);
        } else {
            repeat(now);
        }
    }
    synchronized void repeat(long now) {
        if (done || pupils || start == 0) return;
        holds.get(faceStep).clear(); checks.get(faceStep).clear(); validating = false; retries = 0;
        feedbackReason=faceFailures[faceStep]=0;
        start=now-faceStep*6000000000L*pace; lastFeature=0; peak=0; previousFeatures=null; holdVisible=false;extensions=0;
    }
    synchronized void repeat(int expected, long now) { if(expected==faceStep)repeat(now); }
    synchronized void skip(long now) {
        if (done || pupils || start == 0 || faceStep == 0 || isStageRedo()) return;
        faceFailures[faceStep]=R.string.face_step_skipped;
        holds.get(faceStep).clear(); checks.get(faceStep).clear(); nextFace(now);
    }
    synchronized void skip(int expected, long now) { if(expected==faceStep)skip(now); }
    private void nextFace(long now) {
        feedbackReason=0;
        if (isStageRedo() || faceStep == FaceCalibrationStages.COUNT - 1) {
            done = true; message = getString(R.string.checking_calibration);
        } else {
            faceStep++; validating=false; retries=0; lastFeature=0; peak=0; previousFeatures=null; holdVisible=false;extensions=0;
            start=now-faceStep*6000000000L*pace;
        }
    }
    synchronized void features(byte[] payload, long now) {
        if (done || pupils || start == 0 || heldSince != 0 || now-lastFeature < 250000000L || !holdVisible || now-lastSample > 100000000L) return;
        double t = (now-start)/1e9/pace;
        int step = (int)t/6;
        if (step != faceStep || step >= 12 || t%6 < 2 || t%6 >= 5) return;
        ByteBuffer bytes = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
        float[] values = new float[FEATURES];
        for (int i=0;i<FEATURES;i++) { values[i] = bytes.getFloat(); if (!Float.isFinite(values[i])) { fail(getString(R.string.invalid_model_features)); return; } }
        if (Arrays.equals(previousFeatures, values)) return;
        previousFeatures=values;
        ArrayList<float[]> frames = (validating ? checks : holds).get(step);
        if (lastFeature != 0 && now-lastFeature > 500000000L) frames.clear();
        frames.add(values);
        if (frames.size() > 8) frames.remove(0);
        lastFeature = now;
    }
    synchronized void sample(byte[] payload, long now) {
        if (done) return;
        if (heldSince == 0 && now-heartbeat > 3000000000L) { fail(getString(R.string.calibration_stopped_because_the_app_was_closed)); return; }
        if (!presented || heldSince != 0 || (isStageRedo() && !stagesLoaded)) return;
        lastSample = now;
        ByteBuffer b = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
        if (start == 0) {
            if (pupils) {
                if ((b.getInt(12)&16)==0 || !(((pupilSource&1)!=0 && b.getFloat(104)==1) || ((pupilSource&2)!=0 && b.getFloat(132)==1))) {
                    waiting = getString(R.string.waiting_for_selected_pupil); return;
                }
            } else if ((b.getInt(288)&1)==0) {
                waiting = getString(R.string.cameras_are_active_but_meta_face_tracking_is); return;
            }
            start = now - faceStep * 6000000000L * pace;
        }
        double t = (now-start)/1e9/(pupils?1:pace);
        if (pupils && t>=55+pupilRound*25) { finishPupilRound(now); return; }
        if (pupils && (b.getInt(12)&16)!=0) {
            double[] row = new double[9]; row[0] = t;
            boolean any = false;
            for (int eye=0;eye<2;eye++) {
                int p = 104+eye*28;
                float diameter = b.getFloat(p+24);
                boolean good = b.getFloat(p)==1 && now-previousTime[eye]<=250000000L && Math.abs(diameter-previousDiameter[eye])<=Math.max(3,previousDiameter[eye]*.16);
                row[1+eye*4]=diameter; row[2+eye*4]=b.getFloat(p+4); row[3+eye*4]=b.getFloat(p+8);
                row[4+eye*4]=Math.min(b.getFloat(p+12),b.getFloat(p+16))/Math.max(b.getFloat(p+12),b.getFloat(p+16));
                boolean finite = true; for(int j=1;j<=4;j++) finite &= Double.isFinite(row[eye*4+j]);
                if (b.getFloat(p)==1 && finite) { previousTime[eye]=now; previousDiameter[eye]=diameter; }
                if (!good || !finite || (pupilSource&(1<<eye))==0) Arrays.fill(row,1+eye*4,5+eye*4,Double.NaN);
                else any=true;
            }
            if (any && t>=15) pupilSamples.add(row);
        }
        if (!pupils) {
            double phase = t-faceStep*6;
            if(phase>=5&&phase<6&&extensions<8){
                ArrayList<float[]> frames=(validating?checks:holds).get(faceStep);
                if(frames.size()<8 || faceProblem(faceStep)==R.string.face_hold_unsteady){
                    feedbackReason=faceProblem(faceStep);
                    start+=500000000L*pace;extensions++;phase-=.5;
                }
            }
            holdVisible = false;
            if (phase>=2 && phase<5) {
                float nativeValue = faceStep==1 ? b.getFloat(296+24*4) : faceStep==2 ? Math.max(b.getFloat(296+40*4),b.getFloat(296+41*4)) : faceStep>=6 && faceStep<=10 ? Math.max(b.getFloat(296+68*4),b.getFloat(296+63*4)) : 1;
                float threshold = faceStep>=6 && faceStep<=10 ? .5f : faceStep==1 ? .4f : .3f;
                holdVisible = (b.getInt(288)&1)!=0 && Float.isFinite(nativeValue) && nativeValue>=threshold;
                if ((b.getInt(288)&1)!=0 && Float.isFinite(nativeValue)) peak = Math.max(peak,nativeValue);
            }
            if (phase>=6) {
                int count = (validating ? checks : holds).get(faceStep).size();
                int problem = faceProblem(faceStep);
                assessment = "Pose " + (faceStep+1) + ", attempt " + (retries+1) + ": " + count + " valid samples; peak=" + peak;
                if (problem != 0) {
                    feedbackReason=faceFailures[faceStep]=problem;
                    (validating ? checks : holds).get(faceStep).clear();
                    if (++retries>=2) {
                        if (faceStep==0 || isStageRedo()) { fail(getString(R.string.face_step_failure,getString(PROMPTS[faceStep]),getString(problem))); return; }
                        holds.get(faceStep).clear();checks.get(faceStep).clear();nextFace(now);return;
                    }
                } else if (!validating) { validating=true;retries=0;feedbackReason=faceFailures[faceStep]=0; }
                else { faceFailures[faceStep]=0;nextFace(now);return; }
                start=now-faceStep*6000000000L*pace; lastFeature=0; peak=0;extensions=0;
                previousFeatures=null;holdVisible=false;
            }
        }
    }
    private static double[] thumbnail(ArrayList<float[]> frames) {
        double[] mean=new double[512];
        for(float[] frame:frames) {
            double[] normalized=normalizedThumbnail(frame);
            for(int i=0;i<512;i++)mean[i]+=normalized[i]/frames.size();
        }
        return mean;
    }
    private static double[] normalizedThumbnail(float[] frame) {
        double[] result=new double[512];
        for(int camera=0;camera<2;camera++) {
            int start=camera*256;
            double mean=0,variance=0;
            for(int i=start;i<start+256;i++)mean+=frame[999+i]/256.;
            for(int i=start;i<start+256;i++) {
                result[i]=frame[999+i]-mean;
                variance+=result[i]*result[i]/256.;
            }
            double scale=(32./255)/Math.max(Math.sqrt(variance),1./255);
            for(int i=start;i<start+256;i++)result[i]*=scale;
        }
        return result;
    }
    private static double distance(double[] a,double[] b) {
        double total=0;for(int i=0;i<a.length;i++)total+=Math.abs(a[i]-b[i]);return total/a.length;
    }
    private int faceProblem(int step) {
        ArrayList<float[]> frames=(validating?checks:holds).get(step);
        if(frames.size()<8)return R.string.not_enough_fresh_samples_try_calibration_again;
        double[] mean=thumbnail(frames),neutral=step==0?mean:thumbnail(holds.get(0));
        for(float[] frame:frames)for(int camera=0;camera<2;camera++) {
            double intensity=0;
            for(int i=0;i<256;i++)intensity+=frame[999+camera*256+i]/256.;
            if(intensity<10./255||intensity>230./255)return R.string.face_camera_exposure;
        }
        double movement=distance(mean,neutral);
        if(step!=0&&movement<3./255)return R.string.face_hold_too_similar;
        double[] deviations=new double[frames.size()];
        for(int j=0;j<frames.size();j++){
            double[] thumb=normalizedThumbnail(frames.get(j));
            deviations[j]=distance(thumb,mean);
        }
        // A strong expression must not let a transition into the saved hold.
        if(percentile(deviations,.9)>2.5/255)return R.string.face_hold_unsteady;
        if(validating&&distance(mean,thumbnail(holds.get(step)))>Math.max(3./255,.35*movement))return R.string.face_repeat_mismatch;
        return 0;
    }
    synchronized String faceResult(int capabilities) {
        if (isStageRedo()) return getString(R.string.calibration_stage_saved, getString(PROMPTS[redoStage]));
        ArrayList<String> skipped=new ArrayList<>();
        for(int i=1;i<12;i++)if(holds.get(i).isEmpty())skipped.add(getString(R.string.face_step_failure,
            getString(PROMPTS[i]),getString(faceFailures[i]==0?R.string.not_enough_fresh_samples_try_calibration_again:faceFailures[i])));
        if((capabilities&1)==0)skipped.add(getString(R.string.calibration_tongue_direction));
        if((capabilities&2)==0)skipped.add(getString(R.string.calibration_cheek_separation));
        return skipped.isEmpty()?getString(R.string.face_calibration_passed_and_saved):getString(R.string.face_calibration_partial_details,String.join("\n\n",skipped));
    }
    synchronized int completedPoses() {
        int mask=0;for(int i=0;i<12;i++)if(holds.get(i).size()>=8&&checks.get(i).size()>=8)mask|=1<<i;return mask;
    }
    private static double percentile(double[] a, double q) {
        a=a.clone(); Arrays.sort(a); double x=(a.length-1)*q; int lo=(int)x,hi=(int)Math.ceil(x); return a[lo]+(a[hi]-a[lo])*(x-lo);
    }
    private double[] column(int index, int cycle, int eye) { return pupilSamples.stream().filter(r -> (cycle<0 || (r[0]<55?0:1)==cycle) && Double.isFinite(r[1+eye*4])).mapToDouble(r->r[index]).toArray(); }
    private static double correlation(double[] a, double[] b) {
        double ma=Arrays.stream(a).average().orElse(0),mb=Arrays.stream(b).average().orElse(0),s=0,aa=0,bb=0;
        for(int i=0;i<a.length;i++){double x=a[i]-ma,y=b[i]-mb;s+=x*y;aa+=x*x;bb+=y*y;}return s/Math.sqrt(aa*bb);
    }
    private void finishPupilRound(long now) {
        String problem = pupilProblem(pupilRound,new double[2],new double[2]);
        if (problem.isEmpty() && pupilRound==1) problem=pupilProblem(-1,new double[2],new double[2]);
        assessment="Pupil round " + (pupilRound+1) + ", attempt " + (retries+1) + ": " + (problem.isEmpty() ? "passed" : problem);
        if (problem.isEmpty()) {
            if (pupilRound==1) { done=true; message=getString(R.string.checking_pupil_calibration); }
            else { pupilRound=1; retries=0; start=now-55000000000L; }
        } else if (++retries>=3) {
            fail(getString(R.string.pupil_round_failed, pupilRound+1, problem));
        } else {
            pupilSamples.removeIf(r -> (r[0]<55 ? 0 : 1)==pupilRound);
            start=now-(30+pupilRound*25)*1000000000L;
            Arrays.fill(previousTime,0);
        }
    }
    private String pupilProblem(int cycle,double[] small,double[] large) {
        Arrays.fill(small,10);Arrays.fill(large,60);
        for(int eye=0;eye<2;eye++) {
            if((pupilSource&(1<<eye))==0)continue;
            for(double[] row:pupilSamples) {
                if(!Double.isFinite(row[0]))return getString(R.string.invalid_pupil_measurements);
                boolean missing=true,finite=true;
                for(int j=1;j<=4;j++){missing&=Double.isNaN(row[eye*4+j]);finite&=Double.isFinite(row[eye*4+j]);}
                if(!missing&&!finite)return getString(R.string.invalid_pupil_measurements);
            }
            for(int c=0;c<2;c++)if((cycle<0||c==cycle)&&column(0,c,eye).length<80)
                return getString(R.string.pupils_were_hidden_too_often_adjust_the_headset);
            double[] sizes=column(1+eye*4,0,eye);if(sizes.length<80)return getString(R.string.pupils_were_hidden_too_often_adjust_the_headset);
            small[eye]=percentile(sizes,.05);large[eye]=percentile(sizes,.95);double span=large[eye]-small[eye];
            if(small[eye]<10||large[eye]>60||span<Math.max(3,small[eye]*.1)) return getString(R.string.pupil_size_changed_too_little_reduce_room_light);
            for(int c=0;c<2;c++)if(cycle<0||c==cycle) {
                double[] d=column(1+eye*4,c,eye),levels=column(0,c,eye);for(int i=0;i<levels.length;i++)levels[i]=stimulus(levels[i]-.8);
                if(Math.abs(percentile(d,.05)-small[eye])>.35*span||Math.abs(percentile(d,.95)-large[eye])>.35*span)
                    return getString(R.string.brightness_rounds_did_not_match_keep_your_head);
                if(!(correlation(d,levels)<-.5)) return getString(R.string.pupil_size_did_not_follow_brightness_keep_looking);
            }
            double[] x=column(2+eye*4,cycle,eye),y=column(3+eye*4,cycle,eye),dist=new double[x.length];double mx=percentile(x,.5),my=percentile(y,.5);
            for(int i=0;i<x.length;i++)dist[i]=Math.hypot(x[i]-mx,y[i]-my);
            if(percentile(dist,.9)>15) return getString(R.string.your_gaze_moved_too_much_keep_looking_at);
        }
        return "";
    }
    synchronized boolean save(File directory) throws IOException {
        if (failed) return false;
        if (!pupils) {
            if (isStageRedo() && (!done || !stagesLoaded || holds.get(redoStage).size() < FaceCalibrationStages.SAMPLES
                    || checks.get(redoStage).size() < FaceCalibrationStages.SAMPLES)) {
                fail(getString(R.string.not_enough_fresh_samples_try_calibration_again));
                return false;
            }
            if(holds.get(0).size()<8||checks.get(0).size()<8){fail(getString(R.string.not_enough_fresh_samples_try_calibration_again));return false;}
            for(int i=1;i<12;i++)if(checks.get(i).size()<8){holds.get(i).clear();checks.get(i).clear();}
            ByteBuffer data=ByteBuffer.allocate(FaceCalibrationStages.CAPTURE_HEADER+holds.stream().mapToInt(h->h.size()*FEATURES*4).sum()+checks.stream().mapToInt(h->h.size()*FEATURES*4).sum()).order(ByteOrder.LITTLE_ENDIAN);
            data.putLong(FaceCalibrationStages.CAPTURE_FORMAT);
            for(ArrayList<float[]> hold:holds)data.putInt(hold.size());
            for(ArrayList<float[]> hold:checks)data.putInt(hold.size());
            for(ArrayList<float[]> hold:holds)for(float[] frame:hold)for(float v:frame)data.putFloat(v);
            for(ArrayList<float[]> hold:checks)for(float[] frame:hold)for(float v:frame)data.putFloat(v);
            Files.write(new File(directory,"calibration.bin").toPath(),data.array());
            message=getString(R.string.checking_face_calibration); return true;
        }
        double[] small=new double[2],large=new double[2];
        String problem=pupilProblem(-1,small,large);
        if(!problem.isEmpty()) { fail(problem); return false; }
        Path profile=new File(directory,"profile.bin").toPath(),pending=new File(directory,"profile.pending").toPath();
        byte[] bytes=Files.readAllBytes(profile);ByteBuffer p=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
        if (bytes.length < 32 || (p.getLong(0) != 0x3230305048544651L && p.getLong(0) != 0x3330305048544651L) || p.getInt(12) != bytes.length - 16) throw new IOException(getString(R.string.invalid_headset_calibration_profile));
        if(p.getLong(0)==0x3330305048544651L&&bytes.length<36)throw new IOException(getString(R.string.invalid_headset_calibration_profile));
        if(p.getLong(0)==0x3230305048544651L){bytes=Arrays.copyOf(bytes,bytes.length+4);p=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);if((p.getInt(8)&1)!=0)p.putInt(8,p.getInt(8)|4);}
        p.putLong(0,0x3330305048544651L);p.putInt(12,bytes.length-16);
        p.putInt(8,p.getInt(8)|8);p.position(bytes.length-20);for(double v:small)p.putFloat((float)v);for(double v:large)p.putFloat((float)v);p.putFloat(pupilSource);
        Files.write(pending,bytes);Files.move(pending,profile,StandardCopyOption.REPLACE_EXISTING,StandardCopyOption.ATOMIC_MOVE);
        saved(getString(R.string.pupil_calibration_passed_and_saved));return true;
    }
}
