package com.qftplus.headset;
import java.io.*;
import java.nio.*;
import java.nio.file.*;
import java.util.*;

final class Calibration {
    static final int FEATURES = 999 + 512;
    static final int[] PROMPTS = {R.string.relax_and_look_ahead, R.string.open_your_mouth_wide, R.string.push_your_lips_forward_into_a_kiss, R.string.puff_both_cheeks, R.string.puff_only_your_left_cheek, R.string.puff_only_your_right_cheek, R.string.stick_your_tongue_straight_out, R.string.point_your_tongue_up, R.string.point_your_tongue_down, R.string.point_your_tongue_left, R.string.point_your_tongue_right, R.string.suck_in_both_cheeks};
    private final boolean pupils;
    private long start;
    private long lastFeature, heartbeat = System.nanoTime();
    private long lastSample, heldSince;
    private int faceStep, pupilRound, retries;
    private boolean holdVisible;
    private float peak;
    private String assessment = "";
    private final ArrayList<ArrayList<float[]>> holds = new ArrayList<>();
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
    Calibration(android.content.Context context, boolean pupils) { this.context = context; this.pupils = pupils; waiting = getString(R.string.waiting_for_eye_and_face_cameras); for (int i=0;i<12;i++) holds.add(new ArrayList<>()); }
    boolean isPupils() { return pupils; }
    synchronized void presented() { presented = true; }
    synchronized void heartbeat() { heartbeat = System.nanoTime(); }
    synchronized double elapsed() {
        double t = start == 0 ? 0 : (System.nanoTime() - start) / 1e9;
        return pupils ? Math.min(t,55+pupilRound*25) : Math.min(t, faceStep*6+5.999);
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
        return getString(retries > 0 ? R.string.face_prompt_retry : R.string.face_prompt, getString(PROMPTS[step]), getString(t%6 < 2 ? R.string.get_ready : t%6 < 5 ? R.string.hold_still : R.string.relax));
    }
    synchronized void fail(String reason) { if (!saved) { failed = true; done = true; message = reason; } }
    synchronized void saved(String result) { saved = true; done = true; message = result; }
    synchronized boolean cancelled() { return failed; }
    synchronized void hold(boolean held, long now) {
        if (done || held == (heldSince != 0)) return;
        if (held) { heldSince = now; return; }
        heldSince = 0; lastSample = now;
        if (start == 0) return;
        if (pupils) {
            pupilSamples.removeIf(r -> (r[0]<55 ? 0 : 1)==pupilRound);
            start = now-(pupilRound==0 ? 0 : 55000000000L);
            Arrays.fill(previousTime,0);
        } else {
            holds.get(faceStep).clear();
            start = now-faceStep*6000000000L; lastFeature = 0; peak = 0;
        }
    }
    synchronized void features(byte[] payload, long now) {
        if (done || pupils || start == 0 || now-lastFeature < 250000000L || !holdVisible || now-lastSample > 100000000L) return;
        double t = (now-start)/1e9;
        int step = (int)t/6;
        if (step != faceStep || step >= 12 || t%6 < 2 || t%6 >= 5) return;
        ByteBuffer bytes = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
        float[] values = new float[FEATURES];
        for (int i=0;i<FEATURES;i++) { values[i] = bytes.getFloat(); if (!Float.isFinite(values[i])) { fail(getString(R.string.invalid_model_features)); return; } }
        holds.get(step).add(values); lastFeature = now;
    }
    synchronized void sample(byte[] payload, long now) {
        if (done) return;
        if (now-heartbeat > 3000000000L) { fail(getString(R.string.calibration_stopped_because_the_app_was_closed)); return; }
        if (!presented || heldSince != 0) return;
        lastSample = now;
        ByteBuffer b = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
        if (start == 0) {
            if (pupils) {
                if ((b.getInt(12)&16)==0 || b.getFloat(104)!=1 || b.getFloat(132)!=1) {
                    waiting = getString(R.string.waiting_for_a_clear_view_of_both_pupils); return;
                }
            } else if ((b.getInt(288)&1)==0) {
                waiting = getString(R.string.cameras_are_active_but_meta_face_tracking_is); return;
            }
            start = now;
        }
        double t = (now-start)/1e9;
        if (pupils && t>=55+pupilRound*25) { finishPupilRound(now); return; }
        if (pupils && (b.getInt(12)&16)!=0) {
            double[] row = new double[9]; row[0] = t;
            boolean pair = true;
            for (int eye=0;eye<2;eye++) {
                int p = 104+eye*28;
                float diameter = b.getFloat(p+24);
                boolean good = b.getFloat(p)==1 && now-previousTime[eye]<=250000000L && Math.abs(diameter-previousDiameter[eye])<=Math.max(3,previousDiameter[eye]*.16);
                row[1+eye*4]=diameter; row[2+eye*4]=b.getFloat(p+4); row[3+eye*4]=b.getFloat(p+8);
                row[4+eye*4]=Math.min(b.getFloat(p+12),b.getFloat(p+16))/Math.max(b.getFloat(p+12),b.getFloat(p+16));
                boolean finite = true; for(int j=1;j<=4;j++) finite &= Double.isFinite(row[eye*4+j]);
                if (b.getFloat(p)==1 && finite) { previousTime[eye]=now; previousDiameter[eye]=diameter; }
                pair &= good && finite;
            }
            if (pair && t>=15) pupilSamples.add(row);
        }
        if (!pupils) {
            double phase = t-faceStep*6;
            holdVisible = false;
            if (phase>=2 && phase<5) {
                float nativeValue = faceStep==1 ? b.getFloat(296+24*4) : faceStep==2 ? Math.max(b.getFloat(296+40*4),b.getFloat(296+41*4)) : faceStep>=6 && faceStep<=10 ? Math.max(b.getFloat(296+68*4),b.getFloat(296+63*4)) : 1;
                float threshold = faceStep>=6 && faceStep<=10 ? .5f : faceStep==1 ? .4f : .3f;
                holdVisible = (b.getInt(288)&1)!=0 && Float.isFinite(nativeValue) && nativeValue>=threshold;
                if ((b.getInt(288)&1)!=0 && Float.isFinite(nativeValue)) peak = Math.max(peak,nativeValue);
            }
            if (phase>=6) {
                int count = holds.get(faceStep).size();
                assessment = "Pose " + (faceStep+1) + ", attempt " + (retries+1) + ": " + count + " valid samples; peak=" + peak;
                if (count<8) {
                    holds.get(faceStep).clear();
                    if (++retries>=3) { fail(getString(R.string.pose_capture_failed, getString(PROMPTS[faceStep]))); return; }
                } else { faceStep++; retries=0; }
                start=now-faceStep*6000000000L; lastFeature=0; peak=0;
                if (faceStep==12) { done=true; message=getString(R.string.checking_calibration); }
            }
        }
    }
    private static double percentile(double[] a, double q) {
        a=a.clone(); Arrays.sort(a); double x=(a.length-1)*q; int lo=(int)x,hi=(int)Math.ceil(x); return a[lo]+(a[hi]-a[lo])*(x-lo);
    }
    private double[] column(int index, int cycle) { return pupilSamples.stream().filter(r -> cycle<0 || (r[0]<55?0:1)==cycle).mapToDouble(r->r[index]).toArray(); }
    private static double correlation(double[] a, double[] b) {
        double ma=Arrays.stream(a).average().orElse(0),mb=Arrays.stream(b).average().orElse(0),s=0,aa=0,bb=0;
        for(int i=0;i<a.length;i++){double x=a[i]-ma,y=b[i]-mb;s+=x*y;aa+=x*x;bb+=y*y;}return s/Math.sqrt(aa*bb);
    }
    private void finishPupilRound(long now) {
        String problem = pupilProblem(pupilRound,new double[2],new double[2]);
        if (problem.isEmpty() && pupilRound==1) problem=pupilProblem(-1,new double[2],new double[2]);
        assessment="Pupil round " + (pupilRound+1) + ", attempt " + (retries+1) + ": " + column(0,pupilRound).length + " valid samples; " + (problem.isEmpty() ? "passed" : problem);
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
        for (double[] row : pupilSamples) if(cycle<0 || (row[0]<55 ? 0 : 1)==cycle)
            for (double v : row) if (!Double.isFinite(v)) return getString(R.string.invalid_pupil_measurements);
        for(int c=0;c<2;c++)if((cycle<0||c==cycle)&&column(0,c).length<80)
            return getString(R.string.pupils_were_hidden_too_often_adjust_the_headset);
        for(int eye=0;eye<2;eye++) {
            double[] sizes=column(1+eye*4,cycle);small[eye]=percentile(sizes,.05);large[eye]=percentile(sizes,.95);double span=large[eye]-small[eye];
            if(small[eye]<10||large[eye]>60||span<Math.max(3,small[eye]*.1)) return getString(R.string.pupil_size_changed_too_little_reduce_room_light);
            for(int c=0;c<2;c++)if(cycle<0||c==cycle) {
                double[] d=column(1+eye*4,c),levels=column(0,c);for(int i=0;i<levels.length;i++)levels[i]=stimulus(levels[i]-.8);
                if(Math.abs(percentile(d,.05)-small[eye])>.35*span||Math.abs(percentile(d,.95)-large[eye])>.35*span)
                    return getString(R.string.brightness_rounds_did_not_match_keep_your_head);
                if(!(correlation(d,levels)<-.5)) return getString(R.string.pupil_size_did_not_follow_brightness_keep_looking);
            }
            double[] x=column(2+eye*4,cycle),y=column(3+eye*4,cycle),dist=new double[x.length];double mx=percentile(x,.5),my=percentile(y,.5);
            for(int i=0;i<x.length;i++)dist[i]=Math.hypot(x[i]-mx,y[i]-my);
            if(percentile(dist,.9)>15) return getString(R.string.your_gaze_moved_too_much_keep_looking_at);
        }
        return "";
    }
    synchronized boolean save(File directory) throws IOException {
        if (failed) return false;
        if (!pupils) {
            for (ArrayList<float[]> hold : holds) if (hold.size()<8) { fail(getString(R.string.not_enough_fresh_samples_try_calibration_again)); return false; }
            ByteBuffer data=ByteBuffer.allocate(8+12*4+holds.stream().mapToInt(h->h.size()*FEATURES*4).sum()).order(ByteOrder.LITTLE_ENDIAN);
            data.putLong(0x3230304c41434651L);
            for(ArrayList<float[]> hold:holds)data.putInt(hold.size());
            for(ArrayList<float[]> hold:holds)for(float[] frame:hold)for(float v:frame)data.putFloat(v);
            Files.write(new File(directory,"calibration.bin").toPath(),data.array());
            message=getString(R.string.checking_face_calibration); return true;
        }
        double[] small=new double[2],large=new double[2];
        String problem=pupilProblem(-1,small,large);
        if(!problem.isEmpty()) { fail(problem); return false; }
        Path profile=new File(directory,"profile.bin").toPath(),pending=new File(directory,"profile.pending").toPath();
        byte[] bytes=Files.readAllBytes(profile);ByteBuffer p=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
        if (bytes.length < 32 || p.getLong(0) != 0x3230305048544651L || p.getInt(12) != bytes.length - 16) throw new IOException(getString(R.string.invalid_headset_calibration_profile));
        p.putInt(8,p.getInt(8)|8);p.position(bytes.length-16);for(double v:small)p.putFloat((float)v);for(double v:large)p.putFloat((float)v);
        Files.write(pending,bytes);Files.move(pending,profile,StandardCopyOption.REPLACE_EXISTING,StandardCopyOption.ATOMIC_MOVE);
        saved(getString(R.string.pupil_calibration_passed_and_saved));return true;
    }
}
