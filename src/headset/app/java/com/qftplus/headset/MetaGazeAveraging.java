package com.qftplus.headset;

import android.content.Context;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.concurrent.*;
import java.util.function.Consumer;

final class MetaGazeAveraging implements AutoCloseable {
    static volatile String lastError = "";
    private final Context context;
    private final Consumer<String> status;
    private final Object changed = new Object();
    private final Thread thread;
    private volatile int desired = 25;
    private volatile boolean closed;
    private volatile String cleanupError = "";

    MetaGazeAveraging(Context context, Consumer<String> status) {
        this.context = context.getApplicationContext(); this.status = message -> { lastError = message; status.accept(message); };
        thread = new Thread(this::run, "qft-gaze-averaging"); thread.start();
    }
    void update(int samples) {
        if (samples < 1 || samples > 25) throw new IllegalArgumentException(context.getString(R.string.meta_gaze_averaging_invalid));
        desired = samples;
        synchronized (changed) { changed.notifyAll(); }
    }
    private String problem(String message) {
        return context.getString(message.contains("unsupported") ? R.string.meta_gaze_averaging_unavailable
            : message.contains("busy") ? R.string.meta_gaze_averaging_busy
            : message.contains("restore_failed") ? R.string.meta_gaze_averaging_restore_failed : R.string.meta_gaze_averaging_failed);
    }
    private void run() {
        Session active = null;
        int attempted = -1, sent = -1;
        try {
            while (!closed) {
                int value = desired;
                try {
                    if (!cleanupError.isEmpty()) status.accept(cleanupError);
                    else if (value == 25) {
                        if (active != null) { Session old = active; active = null; old.close(); }
                        status.accept(""); attempted = -1; sent = 25;
                    } else if (active == null && attempted != value) {
                        attempted = value;
                        status.accept(context.getString(R.string.meta_gaze_averaging_applying));
                        active = new Session(); active.start(value); sent = value;
                    } else if (active != null) {
                        if (active.failure != null) throw new IOException(active.failure);
                        if (sent != value) {
                            status.accept(context.getString(R.string.meta_gaze_averaging_applying));
                            active.update(value); sent = value;
                        }
                    }
                } catch (Exception error) {
                    if (String.valueOf(error.getMessage()).contains("restore_failed")) cleanupError = context.getString(R.string.meta_gaze_averaging_restore_failed);
                    if (!closed) status.accept(problem(String.valueOf(error.getMessage())));
                    android.util.Log.w("QFTGaze", "Averaging", error);
                    if (active != null) {
                        try { active.close(); }
                        catch (Exception cleanup) { cleanupError = context.getString(R.string.meta_gaze_averaging_restore_failed); status.accept(cleanupError); }
                        active = null;
                    }
                    attempted = value;
                }
                synchronized (changed) { if (!closed) changed.wait(100); }
            }
        } catch (InterruptedException stopped) {
            if (!closed) Thread.currentThread().interrupt();
        } finally {
            if (active != null) try { active.close(); }
            catch (Exception error) { cleanupError = context.getString(R.string.meta_gaze_averaging_restore_failed); status.accept(cleanupError); }
        }
    }
    public void close() throws IOException {
        boolean interrupted = Thread.interrupted();
        closed = true; thread.interrupt();
        try {
            long deadline = android.os.SystemClock.elapsedRealtime() + 20000;
            while (thread.isAlive()) {
                long remaining = deadline - android.os.SystemClock.elapsedRealtime();
                if (remaining <= 0) break;
                try { thread.join(remaining); }
                catch (InterruptedException error) { interrupted = true; }
            }
            if (thread.isAlive() || !cleanupError.isEmpty()) throw new IOException(context.getString(R.string.meta_gaze_averaging_restore_failed));
        } finally { if (interrupted) Thread.currentThread().interrupt(); }
    }

    private final class Session implements AutoCloseable {
        private Process process;
        private Thread reader;
        private Path directory;
        private final CountDownLatch ready = new CountDownLatch(1);
        private volatile String failure;
        private volatile boolean restored, safeStartFailure, acknowledged;
        void start(int samples) throws Exception {
            directory = Files.createTempDirectory(context.getCacheDir().toPath(), "gaze-averaging-");
            Path script = directory.resolve("gaze_averaging.js");
            try (InputStream input = context.getAssets().open("gaze_averaging.js")) { Files.copy(input, script); }
            String executable = context.getApplicationInfo().nativeLibraryDir + "/libqft_attach.so";
            process = new ProcessBuilder("su", "-c", quote(executable) + " --averaging " + quote(script.toString()) + " " + samples).redirectErrorStream(true).start();
            reader = new Thread(this::read, "qft-gaze-messages"); reader.start();
            if (!ready.await(25, TimeUnit.SECONDS) || failure != null) throw new IOException(failure == null ? "start_failed" : failure);
        }
        void update(int samples) throws IOException {
            process.getOutputStream().write(("samples:" + samples + "\n").getBytes(StandardCharsets.US_ASCII));
            process.getOutputStream().flush();
        }
        private void read() {
            try (BufferedReader reader = new BufferedReader(new InputStreamReader(process.getInputStream(), StandardCharsets.UTF_8))) {
                String line;
                while ((line = reader.readLine()) != null) {
                    if (line.equals("QFT_EYES_READY")) ready.countDown();
                    else if (line.equals("QFT_EYES_RESTORED")) restored = true;
                    else if (line.equals("QFT_EYES_CLOSED")) acknowledged = true;
                    else if (line.startsWith("QFT_EYES_ERROR ")) {
                        failure = line.substring("QFT_EYES_ERROR ".length());
                        safeStartFailure = failure.equals("busy") || failure.equals("unsupported") || failure.equals("payload") || failure.equals("service");
                        ready.countDown();
                    } else if (line.startsWith("QFT_AVERAGING_APPLIED ")) status.accept("");
                    else android.util.Log.i("QFTGaze", line);
                }
                if (!acknowledged && failure == null) failure = "start_failed";
            } catch (IOException error) { failure = String.valueOf(error.getMessage()); }
            finally { ready.countDown(); }
        }
        public void close() throws IOException {
            boolean interrupted = Thread.interrupted();
            try {
                if (process != null) {
                    try { process.getOutputStream().close(); } catch (IOException ignored) {}
                    try {
                        if (!process.waitFor(16, TimeUnit.SECONDS)) throw new IOException("restore_failed");
                        if (reader != null) reader.join(2000);
                        if (!safeStartFailure && (!restored || !acknowledged)) throw new IOException("restore_failed");
                    } catch (InterruptedException error) { interrupted = true; throw new IOException("restore_failed", error); }
                }
            } finally {
                if (directory != null) { Files.deleteIfExists(directory.resolve("gaze_averaging.js")); Files.deleteIfExists(directory); }
                if (interrupted) Thread.currentThread().interrupt();
            }
        }
    }
    private static String quote(String text) { return "'" + text.replace("'", "'\\''") + "'"; }
}
