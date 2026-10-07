package com.qftplus.headset;
import java.io.*;
import java.nio.*;
import java.nio.file.*;
import java.util.*;

final class Calibration {
    static final int FEATURES = 999 + 512;
    static final String[] PROMPTS = {"Relax and look ahead", "Open your mouth wide", "Push your lips forward into a kiss", "Puff both cheeks", "Puff only your left cheek", "Puff only your right cheek", "Stick your tongue straight out", "Point your tongue up", "Point your tongue down", "Point your tongue left", "Point your tongue right", "Suck in both cheeks"};
    private final boolean pupils;
    private long start;
    private long lastFeature, heartbeat = System.nanoTime();
    private long lastSample;
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
    private String waiting = "Waiting for eye and face cameras";
    private final float[] previousDiameter = new float[2];
    private final long[] previousTime = new long[2];
    Calibration(boolean pupils) { this.pupils = pupils; for (int i=0;i<12;i++) holds.add(new ArrayList<>()); }
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
        if (!done && start != 0 && System.nanoTime()-lastSample > 3000000000L)
            fail("Tracking stopped. Check the headset fit and try calibration again.");
        if (done) return message;
        if (start == 0) return waiting;
        double t = elapsed();
        if (pupils) return t < 30 ? "Look at the circle. Let your eyes adjust." : (retries>0 ? "Repeating round " : "Brightness round ") + (pupilRound+1) + " of 2. Keep looking at the circle.";
        int step = Math.min(11, (int)t/6);
        return (retries > 0 ? "Try again · " : "") + PROMPTS[step] + (t%6 < 2 ? " · Get ready" : t%6 < 5 ? " · Hold still" : " · Relax");
    }
    synchronized void fail(String reason) { if (!saved) { failed = true; done = true; message = reason; } }
    synchronized void saved(String result) { saved = true; done = true; message = result; }
    synchronized boolean cancelled() { return failed; }
    synchronized void features(byte[] payload, long now) {
        if (done || pupils || start == 0 || now-lastFeature < 250000000L || !holdVisible || now-lastSample > 100000000L) return;
        double t = (now-start)/1e9;
        int step = (int)t/6;
        if (step != faceStep || step >= 12 || t%6 < 2 || t%6 >= 5) return;
        ByteBuffer bytes = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
        float[] values = new float[FEATURES];
        for (int i=0;i<FEATURES;i++) { values[i] = bytes.getFloat(); if (!Float.isFinite(values[i])) { fail("Invalid model features"); return; } }
        holds.get(step).add(values); lastFeature = now;
    }
    synchronized void sample(byte[] payload, long now) {
        if (done) return;
        if (now-heartbeat > 3000000000L) { fail("Calibration stopped because the app was closed."); return; }
        if (!presented) return;
        lastSample = now;
        ByteBuffer b = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
        if (start == 0) {
            if (pupils) {
                if ((b.getInt(12)&16)==0 || b.getFloat(104)!=1 || b.getFloat(132)!=1) {
                    waiting = "Waiting for a clear view of both pupils"; return;
                }
            } else if ((b.getInt(288)&1)==0) {
                waiting = "Cameras are active, but Meta face tracking is unavailable"; return;
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
                float nativeValue = faceStep==1 ? b.getFloat(296+24*4) : faceStep==2 ? Math.max(b.getFloat(296+40*4),b.getFloat(296+41*4)) : faceStep>=6 && faceStep<=10 ? b.getFloat(296+68*4) : 1;
                float threshold = faceStep>=6 && faceStep<=10 ? .5f : faceStep==1 ? .4f : .3f;
                holdVisible = (b.getInt(288)&1)!=0 && Float.isFinite(nativeValue) && nativeValue>=threshold;
                if ((b.getInt(288)&1)!=0 && Float.isFinite(nativeValue)) peak = Math.max(peak,nativeValue);
            }
            if (phase>=6) {
                int count = holds.get(faceStep).size();
                assessment = "Pose " + (faceStep+1) + ", attempt " + (retries+1) + ": " + count + " valid samples; peak=" + peak;
                if (count<8) {
                    holds.get(faceStep).clear();
                    if (++retries>=3) { fail("Could not capture: " + PROMPTS[faceStep] + ". Adjust the headset and retry. Previous calibration retained."); return; }
                } else { faceStep++; retries=0; }
                start=now-faceStep*6000000000L; lastFeature=0; peak=0;
                if (faceStep==12) { done=true; message="Checking calibration"; }
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
            if (pupilRound==1) { done=true; message="Checking pupil calibration"; }
            else { pupilRound=1; retries=0; start=now-55000000000L; }
        } else if (++retries>=3) {
            fail("Pupil round " + (pupilRound+1) + ": " + problem + " Previous calibration retained.");
        } else {
            pupilSamples.removeIf(r -> (r[0]<55 ? 0 : 1)==pupilRound);
            start=now-(30+pupilRound*25)*1000000000L;
            Arrays.fill(previousTime,0);
        }
    }
    private String pupilProblem(int cycle,double[] small,double[] large) {
        for (double[] row : pupilSamples) if(cycle<0 || (row[0]<55 ? 0 : 1)==cycle)
            for (double v : row) if (!Double.isFinite(v)) return "Invalid pupil measurements.";
        for(int c=0;c<2;c++)if((cycle<0||c==cycle)&&column(0,c).length<80)
            return "Pupils were hidden too often. Adjust the headset so both eyes are visible.";
        for(int eye=0;eye<2;eye++) {
            double[] sizes=column(1+eye*4,cycle);small[eye]=percentile(sizes,.05);large[eye]=percentile(sizes,.95);double span=large[eye]-small[eye];
            if(small[eye]<10||large[eye]>60||span<Math.max(3,small[eye]*.1)) return "Pupil size changed too little. Reduce room-light leaks.";
            for(int c=0;c<2;c++)if(cycle<0||c==cycle) {
                double[] d=column(1+eye*4,c),levels=column(0,c);for(int i=0;i<levels.length;i++)levels[i]=stimulus(levels[i]-.8);
                if(Math.abs(percentile(d,.05)-small[eye])>.35*span||Math.abs(percentile(d,.95)-large[eye])>.35*span)
                    return "Brightness rounds did not match. Keep your head still.";
                if(!(correlation(d,levels)<-.5)) return "Pupil size did not follow brightness. Keep looking at the dot.";
            }
            double[] x=column(2+eye*4,cycle),y=column(3+eye*4,cycle),dist=new double[x.length];double mx=percentile(x,.5),my=percentile(y,.5);
            for(int i=0;i<x.length;i++)dist[i]=Math.hypot(x[i]-mx,y[i]-my);
            if(percentile(dist,.9)>15) return "Your gaze moved too much. Keep looking at the dot.";
        }
        return "";
    }
    synchronized boolean save(File directory) throws IOException {
        if (failed) return false;
        if (!pupils) {
            for (ArrayList<float[]> hold : holds) if (hold.size()<8) { fail("Not enough fresh samples. Try calibration again."); return false; }
            ByteBuffer data=ByteBuffer.allocate(8+12*4+holds.stream().mapToInt(h->h.size()*FEATURES*4).sum()).order(ByteOrder.LITTLE_ENDIAN);
            data.putLong(0x3230304c41434651L);
            for(ArrayList<float[]> hold:holds)data.putInt(hold.size());
            for(ArrayList<float[]> hold:holds)for(float[] frame:hold)for(float v:frame)data.putFloat(v);
            Files.write(new File(directory,"calibration.bin").toPath(),data.array());
            message="Checking face calibration"; return true;
        }
        double[] small=new double[2],large=new double[2];
        String problem=pupilProblem(-1,small,large);
        if(!problem.isEmpty()) { fail(problem); return false; }
        Path profile=new File(directory,"profile.bin").toPath(),pending=new File(directory,"profile.pending").toPath();
        byte[] bytes=Files.readAllBytes(profile);ByteBuffer p=ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
        if (bytes.length < 32 || p.getLong(0) != 0x3230305048544651L || p.getInt(12) != bytes.length - 16) throw new IOException("Invalid headset calibration profile");
        p.putInt(8,p.getInt(8)|8);p.position(bytes.length-16);for(double v:small)p.putFloat((float)v);for(double v:large)p.putFloat((float)v);
        Files.write(pending,bytes);Files.move(pending,profile,StandardCopyOption.REPLACE_EXISTING,StandardCopyOption.ATOMIC_MOVE);
        saved("Pupil calibration passed and saved");return true;
    }
}
