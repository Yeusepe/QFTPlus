package com.qftplus.headset;

import android.app.*;
import android.content.*;
import android.os.*;
import java.lang.Process;
import android.system.Os;
import java.io.*;
import java.net.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.*;
import java.util.concurrent.TimeUnit;

public final class TrackingService extends Service {
    static final String ROOT_NEEDED = "QFT+ Headset needs root access. Allow it in your root manager, then try again.";
    static volatile String status = "Stopped";
    static volatile String error;
    private volatile String workerError;
    static volatile byte[] latest;
    static volatile long lastFrame;
    static volatile Calibration calibration;
    static volatile String lastCalibration = "";
    static volatile boolean calibrating;
    static volatile boolean running;
    static volatile long adjustmentsWanted;
    static volatile byte[] adjustmentsToSend;
    static volatile String pcAdjustments;
    static volatile boolean pcConnected;
    private long adjustmentsSent;
    private volatile boolean stopping;
    private volatile int lastStart;
    private volatile Socket connection;
    private byte[] sessionControl;
    private Thread thread;
    private static final Object runtimeLock = new Object();
    private SharedPreferences settings;
    private File directory;
    private long revision = 1;
    private long outputSequence;
    private HeadsetLink link;
    private OscOutput sessionOsc;
    static File adjustmentsFile(Context context) { return new File(context.getFilesDir(), "output-settings.json"); }
    static String outputMode(SharedPreferences settings) {
        return settings.getString("output", settings.getBoolean("osc", false) ? "raw" : settings.getString("key", "").isEmpty() ? "vrchat" : "pc");
    }
    private boolean direct() { return !outputMode(settings).equals("pc") && !settings.getString("oscHost", "").isEmpty(); }
    private boolean tracking() { return settings.getBoolean("enabled", false); }
    private boolean connected(long now) { return link != null && link.connected(now); }
    private void poll(long now) {
        if (link == null) return;
        try { link.poll(now); } catch (IOException error) { android.util.Log.w("QFT", "PC link", error); }
        pcConnected = link.connected(now);
        if (link.adjustments != null && now - link.adjustmentsTime < 3_000_000_000L) pcAdjustments = link.adjustments;
        byte[] settings = adjustmentsToSend;
        if (settings != null || now - adjustmentsWanted < 2_000_000_000L && now - adjustmentsSent > 250_000_000L) {
            adjustmentsSent = now;
            if (link.adjustments(settings != null ? settings : new byte[0]) && settings != null && adjustmentsToSend == settings) adjustmentsToSend = null;
        }
    }
    private static String quote(String value) { return "'" + value.replace("'", "'\\''") + "'"; }
    public IBinder onBind(Intent intent) { return null; }
    public void onCreate() {
        super.onCreate(); running = true; settings = getSharedPreferences("settings", 0);
        directory = new File(getFilesDir(), "model");
        NotificationManager manager = getSystemService(NotificationManager.class);
        manager.createNotificationChannel(new NotificationChannel("tracking", "Headset tracking", NotificationManager.IMPORTANCE_LOW));
        Intent stop = new Intent(this, TrackingService.class).setAction("stop");
        Notification notification = new Notification.Builder(this, "tracking").setContentTitle("QFT+ headset tracking")
            .setContentText("Processing on this headset. Tap Stop to release tracking resources.")
            .setSmallIcon(android.R.drawable.ic_menu_view)
            .setContentIntent(PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE))
            .addAction(new Notification.Action.Builder(null, "Stop", PendingIntent.getService(this, 0, stop, PendingIntent.FLAG_IMMUTABLE)).build())
            .setOngoing(true).build();
        startForeground(1, notification);
    }
    public int onStartCommand(Intent intent, int flags, int id) {
        if (intent != null && "stop".equals(intent.getAction())) {
            settings.edit().putBoolean("enabled", false).apply(); stopSelf(); return START_NOT_STICKY;
        }
        if (!settings.getBoolean("enabled", false) && calibration == null) { stopSelf(); return START_NOT_STICKY; }
        lastStart = id;
        if (thread == null) {
            error = null; status = "Starting tracking";
            thread = new Thread(() -> {
                for (;;) {
                    int started = lastStart;
                    synchronized (runtimeLock) { if (stopping) return; run(); }
                    if (stopping || !tracking() && stopSelfResult(started)) return;
                }
            }, "qft-runtime");
            thread.start();
        }
        return START_STICKY;
    }
    public void onDestroy() {
        stopping = true; running = false;
        try { if (connection != null) connection.close(); } catch (IOException ignored) {}
        if (thread != null) thread.interrupt();
        Calibration active = calibration;
        if (active != null) active.fail("Tracking stopped. Previous calibration retained.");
        calibration = null; latest = null; status = "Stopped"; pcConnected = false;
        pcAdjustments = null; adjustmentsToSend = null;
        super.onDestroy();
    }
    private Process root(String command) throws IOException {
        return new ProcessBuilder("su", "-c", command).redirectErrorStream(true).start();
    }
    private void command(String command, int seconds) throws Exception {
        Process p = root(command);
        ByteArrayOutputStream log = new ByteArrayOutputStream();
        Thread reader = new Thread(() -> {
            try (InputStream input = p.getInputStream()) {
                byte[] b = new byte[2048]; int n;
                while ((n = input.read(b)) > 0) if (log.size() < 16384) log.write(b, 0, n);
            } catch (IOException ignored) {}
        });
        reader.start();
        try {
            if (!p.waitFor(seconds, TimeUnit.SECONDS)) throw new IOException("Headset setup timed out");
            reader.join(1000);
            if (p.exitValue() != 0) throw new IOException(log.toString(StandardCharsets.UTF_8.name()));
        } finally { p.destroy(); }
    }
    private void prepare() throws Exception {
        status = "Preparing headset model";
        Files.createDirectories(directory.toPath());
        File stamp = new File(directory, "model.version");
        byte[] installed = String.valueOf(getPackageManager().getPackageInfo(getPackageName(), 0).lastUpdateTime).getBytes(StandardCharsets.UTF_8);
        boolean update = !stamp.exists() || !java.util.Arrays.equals(Files.readAllBytes(stamp.toPath()), installed);
        boolean newHeads = false;
        for (String name : new String[]{"model.bin", "tail.onnx", "heads.bin"}) {
            File target = new File(directory, name);
            if (update || !target.exists()) {
                Path pending = new File(directory, name + ".tmp").toPath();
                try (InputStream input = getAssets().open("model/" + name)) { Files.copy(input, pending, StandardCopyOption.REPLACE_EXISTING); }
                if (name.equals("heads.bin") && target.exists()) newHeads = !java.util.Arrays.equals(Files.readAllBytes(pending), Files.readAllBytes(target.toPath()));
                Files.move(pending, target.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
            }
        }
        if (update) Files.write(stamp.toPath(), installed);
        File profile = new File(directory, "profile.bin");
        if (!profile.exists() || !ControlReceiver.currentProfile(profile) || newHeads) {
            byte[] old = profile.exists() ? Files.readAllBytes(profile.toPath()) : null, fresh;
            try (InputStream input = getAssets().open("model/profile.bin")) { fresh = input.readAllBytes(); }
            ByteBuffer header = ByteBuffer.wrap(fresh).order(ByteOrder.LITTLE_ENDIAN);
            if (old != null && old.length >= 32 && (ByteBuffer.wrap(old).order(ByteOrder.LITTLE_ENDIAN).getInt(8) & 8) != 0) {
                System.arraycopy(old, old.length - 16, fresh, fresh.length - 16, 16);
                header.putInt(8, header.getInt(8) | 8);
            }
            OutputAdjustments.write(profile, fresh);
            if (old != null) settings.edit().putBoolean("tongue", false).apply();
        }
        File ort = new File(directory, "libonnxruntime.so");
        Files.deleteIfExists(ort.toPath());
        Os.symlink(getApplicationInfo().nativeLibraryDir + "/libonnxruntime.so", ort.toString());
    }
    private String environment() {
        return "LD_LIBRARY_PATH=" + quote(getApplicationInfo().nativeLibraryDir + ":/vendor/lib64") + " ";
    }
    private void run() {
        android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_DISPLAY);
        android.net.wifi.WifiManager.WifiLock wifi = getSystemService(android.net.wifi.WifiManager.class)
            .createWifiLock(android.net.wifi.WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "qft-tracking");
        wifi.setReferenceCounted(false); wifi.acquire();
        try {
            status = "Checking root access";
            try { command("id", 30); } catch (IOException denied) { throw new IOException(ROOT_NEEDED, denied); }
            prepare();
            String pairKey = settings.getString("key", "");
            link = null;
            try (Convergence eyes = settings.getBoolean("convergence", true) ? Convergence.start(this) : null) {
                while (!stopping) {
                    if (!tracking() && calibration == null) break;
                    if (link == null && tracking() && outputMode(settings).equals("pc") && !pairKey.isEmpty())
                        link = new HeadsetLink(pairKey, settings.getString("host", ""), settings.getInt("port", 27276));
                    poll(System.nanoTime());
                    if (!connected(System.nanoTime()) && !direct() && calibration == null) {
                        status = outputMode(settings).equals("pc") ? "Waiting for QFT+ on your PC" : "Choose a destination in Connections";
                        Thread.sleep(500);
                        continue;
                    }
                    session(eyes);
                }
            }
        } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }
        catch (Exception failure) {
            if (!stopping) {
                error = workerError != null ? workerError : failure.getMessage() != null ? failure.getMessage() : failure.toString();
                Calibration c = calibration;
                if (c != null) { c.fail(error + " Your previous calibration is unchanged."); calibration = null; }
                status = "Tracking stopped: " + error;
                settings.edit().putBoolean("enabled", false).apply();
                android.util.Log.e("QFT", status, failure);
                try { Files.write(new File(getFilesDir(), "status.txt").toPath(), status.getBytes(StandardCharsets.UTF_8)); } catch (IOException ignored) {}
            }
        }
        finally {
            wifi.release();
            if (link != null) link.close(); latest = null;
            try {
                Files.deleteIfExists(new File(directory, "calibration.bin").toPath());
                Files.deleteIfExists(new File(directory, "profile.pending").toPath());
            } catch (IOException error) { android.util.Log.e("QFT", "Calibration temporary-file cleanup failed", error); }
        }
    }
    private byte[] exact(InputStream input, int count, Convergence eyes) throws IOException {
        byte[] data = new byte[count]; int offset = 0;
        long deadline = SystemClock.elapsedRealtime() + 10000;
        while (offset < count && !stopping) {
            try {
                int n = input.read(data, offset, count - offset);
                if (n < 0) throw new EOFException("Headset worker stopped");
                offset += n;
            } catch (SocketTimeoutException error) {
                if (eyes != null) eyes.check();
                status = "Waiting for eye and face cameras";
                if (sessionOsc != null) sessionOsc.idle();
                if (sessionControl != null) connection.getOutputStream().write(sessionControl);
                poll(System.nanoTime());
                if (!connected(System.nanoTime()) && !direct() && calibration == null) return null;
                if (offset > 0 && SystemClock.elapsedRealtime() >= deadline) throw new IOException("Incomplete camera frame");
            }
        }
        if (stopping) throw new EOFException("Stopped");
        return data;
    }
    private void session(Convergence eyes) throws Exception {
        String libraries = getApplicationInfo().nativeLibraryDir;
        workerError = null;
        java.util.concurrent.atomic.AtomicBoolean started = new java.util.concurrent.atomic.AtomicBoolean();
        byte[] random = new byte[32]; new SecureRandom().nextBytes(random);
        StringBuilder key = new StringBuilder(); for (byte b : random) key.append(String.format("%02x", b & 255));
        Process worker = root(environment() + "exec " + quote(libraries + "/libqft_worker.so") + " --app-session --model " + quote(directory.toString()) + " --max-fps " + (settings.getInt("rate", 0) > 0 ? settings.getInt("rate", 0) : 72) + " --token " + key);
        Thread log = new Thread(() -> {
            try (BufferedReader reader = new BufferedReader(new InputStreamReader(worker.getInputStream()))) {
                String line; while ((line = reader.readLine()) != null) {
                    android.util.Log.i("QFTWorker", line);
                    if (line.startsWith("QFT_ERROR: ")) workerError = line.substring(11);
                    if (!started.get() && (line.contains("FAILED") || line.contains("UNSUPPORTED") || line.contains("CANNOT LINK") || line.contains("not found") || line.contains("No such file"))) status = line;
                }
            } catch (IOException ignored) {}
        });
        log.start();
        try (OscOutput osc = tracking() && direct() ? new OscOutput(this, settings.getString("oscHost", ""), settings.getInt("oscPort", 9000), outputMode(settings).equals("vrchat"), adjustmentsFile(this)) : null) {
            sessionOsc = osc;
            command(quote(libraries + "/libqft_attach.so") + " " + quote(libraries + "/libqft_capture_export.so") + " " + key, 25);
            Socket socket = null;
            for (int i = 0; i < 90 && !stopping; i++) {
                if (!worker.isAlive()) throw new IOException("Worker exited " + worker.exitValue() + ": " + status);
                try { socket = new Socket(); socket.connect(new InetSocketAddress("127.0.0.1", 27273), 200); break; }
                catch (IOException error) { if (socket != null) socket.close(); socket = null; Thread.sleep(500); }
            }
            if (socket == null) throw new IOException("Headset worker did not start");
            connection = socket; started.set(true); socket.setSoTimeout(1000); socket.setTcpNoDelay(true);
            OutputStream output = socket.getOutputStream(); InputStream input = socket.getInputStream();
            output.write(key.toString().getBytes(StandardCharsets.US_ASCII));
            ByteBuffer control = ByteBuffer.allocate(312).order(ByteOrder.LITTLE_ENDIAN);
            long sequence = -1;
            long perfAt = SystemClock.elapsedRealtime() + 5000, statusAt = 0; int perfFrames = 0, perfStale = 0, perfFlags = 0, perfStatus = 0, perfNative = 0;
            double perfLatency = 0, perfOutput = 0, perfWorker = 0; float tongueMax = 0, puffMax = 0, metaTongue = 0, metaPuff = 0;
            int capabilities = ControlReceiver.capabilities(this);
            if ((capabilities & 1) == 0 && settings.getBoolean("tongue", false)) settings.edit().putBoolean("tongue", false).apply();
            if ((capabilities & 8) == 0 && settings.getBoolean("pupils", false)) settings.edit().putBoolean("pupils", false).apply();
            while (!stopping) {
                if (eyes != null) eyes.check();
                Calibration c = calibration;
                int options = 1 | 8 | 16;
                if ((capabilities & 1) != 0 && settings.getBoolean("tongue", false)) options |= 2;
                if ((capabilities & 8) != 0 && settings.getBoolean("pupils", false)) options |= 4;
                if (c != null && c.isPupils()) options |= 64;
                poll(System.nanoTime());
                if (c == null && (!tracking() || !connected(System.nanoTime()) && osc == null)) break;
                control.clear(); control.putInt(0x43544651).putInt(3).putInt(c != null && !c.isPupils() ? 24 : 0).putInt((int)revision);
                control.position(24); control.putFloat(1000); control.position(308); control.putInt(options);
                sessionControl = control.array();
                output.write(sessionControl);
                byte[] header = exact(input, 64, eyes);
                if (header == null) break;
                ByteBuffer h = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN);
                if (h.getLong(0) != 0x00334556494c5051L || h.getInt(8) != 3 || h.getInt(12) != 64) throw new IOException("Invalid headset header");
                int kind = h.getInt(44), bytes = h.getInt(48);
                if (!((kind == 4 && bytes == 608) || (kind == 5 && bytes == Calibration.FEATURES * 4))) throw new IOException("Invalid headset payload");
                byte[] payload = exact(input, bytes, eyes);
                if (payload == null) break;
                long captured = h.getLong(24), finished = h.getLong(56);
                long now = System.nanoTime();
                if (captured > finished || finished > now || now - captured > 250000000L) { perfStale++; continue; }
                if (kind == 5) { if (c != null && !c.isPupils() && h.getLong(16) == sequence) c.features(payload, now); continue; }
                if (h.getLong(16) <= sequence) continue;
                sequence = h.getLong(16);
                ByteBuffer result = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
                if (result.getInt(0) != 2 || result.getInt(8) != (int)revision) continue;
                boolean converging = eyes != null && eyes.apply(result, now, settings.getFloat("vergenceGain", 1));
                latest = payload; lastFrame = SystemClock.elapsedRealtime();
                int nativeFlags = result.getInt(288);
                if (now - statusAt > 500_000_000L) {
                    statusAt = now;
                    status = "Tracking on headset · " + Math.round((now - captured) / 1e6) + " ms capture-to-output";
                    if ((nativeFlags & 1) == 0) status += " · Meta face tracking unavailable";
                    if ((nativeFlags & 6) != 6) status += " · Meta eye gaze unavailable";
                    if (eyes != null) status += converging ? " · Convergence on" : " · Waiting for convergence";
                }
                long outputStart = System.nanoTime();
                boolean sending = tracking();
                if (link != null && sending) link.tracking(payload, ++outputSequence, captured, now);
                if (osc != null && sending) osc.send(payload, sequence, (now - captured) / 1e6);
                perfFrames++; perfLatency += (now - captured) / 1e6; perfWorker += (finished - captured) / 1e6; perfOutput += (System.nanoTime() - outputStart) / 1e6;
                perfFlags = result.getInt(4); perfStatus = result.getInt(12); perfNative = nativeFlags;
                tongueMax = Math.max(tongueMax, result.getFloat(84)); puffMax = Math.max(puffMax, Math.max(result.getFloat(20), result.getFloat(24)));
                metaTongue = Math.max(metaTongue, result.getFloat(296 + 68 * 4)); metaPuff = Math.max(metaPuff, Math.max(result.getFloat(296 + 2 * 4), result.getFloat(296 + 3 * 4)));
                if (SystemClock.elapsedRealtime() >= perfAt) {
                    android.util.Log.i("QFT", String.format(java.util.Locale.ROOT,
                        "perf: %.1f fps, %d stale, %.0f ms capture-to-output (worker %.0f), output %.2f ms/frame (expressions %.2f, adjustments %.2f, send %.2f), %d OSC msgs/s, VRChat round trip %.0f ms, flags=%x status=%x native=%x, tongue max %.2f (Meta %.2f), puff max %.2f (Meta %.2f)",
                        perfFrames / 5.0, perfStale, perfFrames > 0 ? perfLatency / perfFrames : 0, perfFrames > 0 ? perfWorker / perfFrames : 0, perfFrames > 0 ? perfOutput / perfFrames : 0,
                        osc != null && perfFrames > 0 ? osc.readNs / 1e6 / perfFrames : 0, osc != null && perfFrames > 0 ? osc.applyNs / 1e6 / perfFrames : 0,
                        osc != null && perfFrames > 0 ? osc.sendNs / 1e6 / perfFrames : 0,
                        osc != null ? osc.messages / 5 : 0, osc != null ? osc.roundTrip() : -1, perfFlags, perfStatus, perfNative, tongueMax, metaTongue, puffMax, metaPuff));
                    if (osc != null) { osc.messages = 0; osc.readNs = osc.applyNs = osc.sendNs = 0; }
                    perfAt = SystemClock.elapsedRealtime() + 5000; perfFrames = perfStale = 0; perfLatency = perfOutput = perfWorker = 0; tongueMax = puffMax = metaTongue = metaPuff = 0;
                }
                if (!connected(now) && osc == null && c == null) status = "Waiting for QFT+ on your PC";
                if (c != null) {
                    c.sample(payload, now);
                    if (c.done) {
                        boolean restart = false;
                        try { if (c.save(directory)) {
                            if (!c.isPupils()) {
                                restart = true; latest = null;
                                status = "Saving face calibration";
                                socket.close();
                                if (!worker.waitFor(3, TimeUnit.SECONDS))
                                    command(environment() + quote(libraries + "/libqft_worker.so") + " --stop-token " + key, 8);
                                try { command(environment() + quote(libraries + "/libqft_calibrate.so") + " " + quote(directory.toString()), 45); }
                                catch (IOException fit) {
                                    String why = String.valueOf(fit.getMessage()).trim(); int at = why.lastIndexOf("CALIBRATION_FAILED ");
                                    why = at >= 0 ? why.substring(at + 19) : why.substring(why.lastIndexOf('\n') + 1);
                                    c.fail(why.trim().replaceAll("\\.$", "") + ". Previous calibration retained.");
                                }
                                synchronized (c) {
                                    if (c.cancelled()) Files.deleteIfExists(new File(directory, "profile.pending").toPath());
                                    else {
                                        Files.move(new File(directory, "profile.pending").toPath(), new File(directory, "profile.bin").toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
                                        settings.edit().putBoolean("tongue", true).apply();
                                        c.saved("Face calibration passed and saved");
                                    }
                                }
                            } else settings.edit().putBoolean("pupils", true).apply();
                            revision++; capabilities = ControlReceiver.capabilities(this);
                        } } finally {
                            Files.deleteIfExists(new File(directory, "calibration.bin").toPath());
                            Files.deleteIfExists(new File(directory, "profile.pending").toPath());
                        }
                        if (calibration == c) calibration = null;
                        if (restart) return;
                    }
                }
            }
        } finally {
            Socket socket = connection; connection = null;
            Thread.interrupted();
            sessionControl = null;
            sessionOsc = null;
            try {
                if (socket != null) socket.close();
                worker.getOutputStream().close();
            } finally {
                try {
                    if (!worker.waitFor(3, TimeUnit.SECONDS))
                        command(environment() + quote(libraries + "/libqft_worker.so") + " --stop-token " + key, 8);
                } finally {
                    worker.destroy(); log.join(1000);
                    try { command(environment() + quote(libraries + "/libqft_worker.so") + " --release-features " + quote(directory.toString()), 8); }
                    catch (Exception error) { android.util.Log.w("QFT", "Releasing Meta face and eye tracking failed", error); }
                    if (!worker.isAlive() && worker.exitValue() == 6) {
                        removeCalibration(this); prepare();
                        throw new IOException("Your calibration couldn't be loaded and was reset. Calibrate again in QFT+ Headset.");
                    }
                }
            }
        }
    }

    static void removeCalibration(Context context) throws IOException {
        synchronized (runtimeLock) {
            File dir = new File(context.getFilesDir(), "model");
            for (String name : new String[]{"profile.bin", "profile.pending", "calibration.bin"}) Files.deleteIfExists(new File(dir, name).toPath());
            context.getSharedPreferences("settings", 0).edit().putBoolean("tongue", false).putBoolean("pupils", false).commit();
        }
    }
}
