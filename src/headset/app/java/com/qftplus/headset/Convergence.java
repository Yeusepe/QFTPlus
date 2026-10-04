package com.qftplus.headset;

import android.content.Context;
import java.io.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.regex.*;
import java.util.zip.*;
import org.json.*;

final class Convergence implements AutoCloseable {
    private static final String ENGINE = "/odm/lib64/libtrackingengines.so";
    private static final String MODEL = "/odm/etc/eyetracking/runtime/models/Seacliff_V1_5/fbnet/int8/experimental/bolt/bolt.ptl";
    private static final String OLD = "\"id\": 52, \"name\": \"211_reshape\", \"op\": \"OP_Reshape\", \"padding\": \"NN_PAD_NA\", \"input\": [[50, 0], [51, 0]]";
    private static final Pattern SAMPLE = Pattern.compile("(\\d+\\.\\d+): detector_output(?:_secondary)?: .*?x=0x([0-9a-fA-F]+) y=0x([0-9a-fA-F]+) z=0x([0-9a-fA-F]+) tag=0x([0-9a-fA-F]+) valid=0x([0-9a-fA-F]+)");
    private final double[][][] coefficients = new double[2][3][2];
    private final int[] tags = new int[2];
    private final double[][] pending = new double[2][];
    private final double[] times = new double[2], filtered = new double[2], derivative = new double[2];
    private double filterTime;
    private volatile double[] latest;
    private volatile String failure;
    private final CountDownLatch ready = new CountDownLatch(1);
    private Process process;
    private Thread reader;
    private Path directory;
    private volatile boolean closed;

    Convergence(JSONObject calibration) throws JSONException {
        if (!calibration.getString("format").equals("qpro-independent-personalized-visual-axis-v2") || !calibration.getJSONObject("quality_gate").getBoolean("gaze_pass"))
            throw new IllegalArgumentException("The eye calibration did not pass its gaze check");
        JSONObject mapping = calibration.optJSONObject("detector_tag_mapping");
        for (int eye = 0; eye < 2; eye++) {
            String name = eye == 0 ? "left" : "right";
            String tag = mapping == null ? "trace_tag_" + eye : mapping.optString("physical_" + name, "trace_tag_" + eye);
            if (!tag.equals("trace_tag_0") && !tag.equals("trace_tag_1")) throw new IllegalArgumentException("Invalid eye tag mapping");
            tags[eye] = tag.charAt(tag.length() - 1) - '0';
            JSONArray rows = calibration.getJSONObject(name).getJSONArray("coefficients");
            if (rows.length() != 3) throw new IllegalArgumentException("Invalid eye calibration");
            for (int row = 0; row < 3; row++) {
                JSONArray values = rows.getJSONArray(row);
                if (values.length() != 2) throw new IllegalArgumentException("Invalid eye calibration");
                for (int axis = 0; axis < 2; axis++) {
                    double value = values.getDouble(axis);
                    if (!Double.isFinite(value)) throw new IllegalArgumentException("Invalid eye calibration");
                    coefficients[eye][row][axis] = value;
                }
            }
        }
    }
    private static String quote(String text) { return "'" + text.replace("'", "'\\''") + "'"; }
    private static byte[] rootFile(String path) throws Exception {
        Process p = new ProcessBuilder("su", "-c", "cat " + quote(path)).redirectError(new File("/dev/null")).start();
        ExecutorService executor = Executors.newSingleThreadExecutor();
        try {
            Future<byte[]> result = executor.submit(() -> { try (InputStream input = p.getInputStream()) { return input.readAllBytes(); } });
            byte[] data = result.get(30, TimeUnit.SECONDS);
            if (!p.waitFor(5, TimeUnit.SECONDS) || p.exitValue() != 0) throw new IOException("Cannot read headset eye tracking files. Allow QFT+ root access.");
            return data;
        } finally { p.destroy(); executor.shutdownNow(); }
    }
    static Convergence start(Context context) throws Exception {
        JSONObject calibration;
        try (InputStream input = context.getAssets().open("calibration/qpro-independent-visual-axis-v2.json")) {
            calibration = new JSONObject(new String(input.readAllBytes(), StandardCharsets.UTF_8));
        }
        Convergence eyes = new Convergence(calibration);
        try {
            EyeProbe probe = new EyeProbe(rootFile(ENGINE));
            eyes.directory = Files.createTempDirectory(context.getCacheDir().toPath(), "convergence-");
            Path model = eyes.directory.resolve("bolt.ptl"), script = eyes.directory.resolve("convergence.sh");
            patch(rootFile(MODEL), model);
            try (InputStream input = context.getAssets().open("convergence.sh")) { Files.copy(input, script); }
            eyes.process = new ProcessBuilder("su", "-c", "sh " + quote(script.toString()) + " " + quote(model.toString()) + " " +
                Integer.toHexString(probe.offsets[0]) + " " + Integer.toHexString(probe.offsets[1]) + " " + quote(probe.fetch())).redirectErrorStream(true).start();
            eyes.reader = new Thread(eyes::read, "qft-convergence"); eyes.reader.start();
            if (!eyes.ready.await(30, TimeUnit.SECONDS)) throw new IOException("Convergence startup timed out");
            eyes.check();
            return eyes;
        } catch (Exception error) { eyes.close(); throw error; }
    }
    private static void contract(JSONObject graph, boolean patched) throws JSONException {
        Map<Integer, JSONObject> nodes = new HashMap<>(); JSONArray list = graph.getJSONArray("node");
        for (int i = 0; i < list.length(); i++) { JSONObject n = list.getJSONObject(i); nodes.put(n.getInt("id"), n); }
        if (!nodes.containsKey(18) || !nodes.containsKey(52) ||
            !nodes.get(18).getJSONArray("output").getJSONObject(0).getJSONArray("shape").toString().equals("[2,2]") ||
            !nodes.get(52).getJSONArray("input").toString().equals(patched ? "[[18,0],[51,0]]" : "[[50,0],[51,0]]") ||
            !nodes.get(52).getJSONArray("output").getJSONObject(0).getJSONArray("shape").toString().equals("[1,4]"))
            throw new IllegalArgumentException("Unsupported independent eye model layout");
        JSONArray outputs = graph.getJSONArray("output"); String[] shapes = {"[1,4]", "[1,6]", "[1,6]", "[1,1]", "[1,1]"};
        if (outputs.length() != shapes.length) throw new IllegalArgumentException("Unsupported eye model outputs");
        for (int i = 0; i < shapes.length; i++) if (!outputs.getJSONObject(i).getJSONArray("shape").toString().equals(shapes[i]))
            throw new IllegalArgumentException("Unsupported eye model output shape");
    }
    static void patch(byte[] source, Path destination) throws Exception {
        boolean found = false;
        ByteBuffer zip = ByteBuffer.wrap(source).order(ByteOrder.LITTLE_ENDIAN);
        int end = source.length - 22, wide = end - 76;
        if (wide >= 0 && zip.getInt(end) == 0x06054b50 && zip.getShort(end + 20) == 0 && zip.getInt(wide) == 0x06064b50 &&
            zip.getLong(wide + 4) == 44 && zip.getInt(end - 20) == 0x07064b50 && zip.getLong(end - 12) == wide &&
            zip.getLong(wide + 24) == Short.toUnsignedInt(zip.getShort(end + 8)) &&
            zip.getLong(wide + 32) == Short.toUnsignedInt(zip.getShort(end + 10)) &&
            zip.getLong(wide + 40) == Integer.toUnsignedLong(zip.getInt(end + 12)) &&
            zip.getLong(wide + 48) == Integer.toUnsignedLong(zip.getInt(end + 16))) {
            byte[] ordinary = Arrays.copyOf(source, source.length - 76);
            System.arraycopy(source, end, ordinary, wide, 22); source = ordinary;
        }
        Path stock = Files.createTempFile(destination.getParent(), "stock-", ".ptl");
        try {
            Files.write(stock, source);
            try (ZipFile input = new ZipFile(stock.toFile()); ZipOutputStream output = new ZipOutputStream(Files.newOutputStream(destination))) {
                Enumeration<? extends ZipEntry> entries = input.entries();
                while (entries.hasMoreElements()) {
                    ZipEntry entry = entries.nextElement();
                    byte[] bytes;
                    try (InputStream data = input.getInputStream(entry)) { bytes = data.readAllBytes(); }
                    if (entry.getName().equals("model/data.pkl")) {
                        if (found) throw new IOException("Duplicate eye model graph"); found = true;
                        String pickle = new String(bytes, StandardCharsets.ISO_8859_1);
                        int start = pickle.indexOf("{\"version\": \"HEXAGON");
                        if (start < 0) throw new IOException("Missing eye model graph");
                        JSONObject graph = (JSONObject)new JSONTokener(pickle.substring(start)).nextValue();
                        contract(graph, false);
                        if (pickle.indexOf(OLD) < 0 || pickle.indexOf(OLD) != pickle.lastIndexOf(OLD)) throw new IOException("Eye model reshape is not unique");
                        pickle = pickle.replace(OLD, OLD.replace("[[50, 0]", "[[18, 0]"));
                        contract((JSONObject)new JSONTokener(pickle.substring(start)).nextValue(), true);
                        bytes = pickle.getBytes(StandardCharsets.ISO_8859_1);
                    }
                    ZipEntry saved = new ZipEntry(entry.getName()); saved.setMethod(entry.getMethod());
                    if (saved.getMethod() == ZipEntry.STORED) {
                        CRC32 crc = new CRC32(); crc.update(bytes); saved.setSize(bytes.length); saved.setCrc(crc.getValue());
                    }
                    output.putNextEntry(saved); output.write(bytes); output.closeEntry();
                }
            }
        } finally { Files.deleteIfExists(stock); }
        if (!found) throw new IOException("Missing eye model graph");
    }
    private void read() {
        try (BufferedReader input = new BufferedReader(new InputStreamReader(process.getInputStream(), StandardCharsets.UTF_8))) {
            String line;
            while ((line = input.readLine()) != null) {
                if (line.equals("QFT_CONVERGENCE_READY")) ready.countDown();
                else if (line.startsWith("QFT_CONVERGENCE_ERROR:")) failure = line.substring(line.indexOf(':') + 1).trim();
                else sample(line);
            }
            if (!closed && failure == null) failure = "The headset convergence reader stopped";
        } catch (Exception error) { if (!closed) failure = error.getMessage(); }
        finally { ready.countDown(); }
    }
    private static double alpha(double cutoff, double dt) { double rate = 2 * Math.PI * cutoff * dt; return rate / (rate + 1); }
    void sample(String line) {
        Matcher m = SAMPLE.matcher(line); if (!m.find()) return;
        int tag = (int)(Long.parseLong(m.group(5), 16) & 255); if (tag > 1) return;
        double[] vector = new double[3]; double norm = 0;
        for (int i = 0; i < 3; i++) { vector[i] = Float.intBitsToFloat((int)Long.parseLong(m.group(i + 2), 16)); norm += vector[i] * vector[i]; }
        if (Long.parseLong(m.group(6), 16) != 1 || !Double.isFinite(norm) || norm < .25 || norm > 2.25) { pending[tag] = null; return; }
        pending[tag] = vector; times[tag] = Double.parseDouble(m.group(1));
        if (pending[0] == null || pending[1] == null) return;
        if (Math.abs(times[0] - times[1]) > .004) { pending[times[0] < times[1] ? 0 : 1] = null; return; }
        double time = (times[0] + times[1]) * .5;
        double[] angles = new double[4];
        for (int eye = 0; eye < 2; eye++) {
            double[] v = pending[tags[eye]];
            double yaw = Math.toDegrees(Math.atan2(v[0], v[2])), pitch = Math.toDegrees(Math.atan2(-v[1], Math.hypot(v[0], v[2])));
            for (int axis = 0; axis < 2; axis++) angles[eye * 2 + axis] = coefficients[eye][0][axis] + yaw * coefficients[eye][1][axis] + pitch * coefficients[eye][2][axis];
        }
        Arrays.fill(pending, null);
        double dt = Math.min(.25, time - filterTime);
        double[] shared = new double[2];
        for (int axis = 0; axis < 2; axis++) {
            shared[axis] = (angles[axis] + angles[2 + axis]) * .5;
            double gap = (angles[axis] - angles[2 + axis]) * .5;
            if (filterTime == 0) filtered[axis] = gap;
            else if (dt > 1e-6) {
                derivative[axis] += alpha(.5, dt) * ((gap - filtered[axis]) / dt - derivative[axis]);
                filtered[axis] += alpha(1 + .03 * Math.abs(derivative[axis]), dt) * (gap - filtered[axis]);
            }
        }
        filterTime = time;
        latest = new double[]{shared[0] + filtered[0], shared[1] + filtered[1], shared[0] - filtered[0], shared[1] - filtered[1], time};
    }
    void check() throws IOException { if (failure != null) throw new IOException("Convergence: " + failure + ". Turn off Convergence to use standard gaze."); }
    boolean apply(ByteBuffer packet, long now, float gain) {
        double[] eye = latest;
        boolean fresh = eye != null && now / 1e9 >= eye[4] && now / 1e9 - eye[4] <= .25;
        packet.putInt(288, (packet.getInt(288) & ~6) | (fresh ? 6 : 0));
        if (!fresh) return false;
        gain = Float.isFinite(gain) ? Math.max(0, Math.min(3, gain)) : 1;
        for (int side = 0; side < 2; side++) {
            int source = (1 - side) * 2;
            double yaw = Math.toRadians((eye[0] + eye[2]) * .5 + (eye[source] - (eye[0] + eye[2]) * .5) * gain);
            double pitch = Math.toRadians((eye[1] + eye[3]) * .5 + (eye[source + 1] - (eye[1] + eye[3]) * .5) * gain);
            double sy = Math.sin(yaw * .5), cy = Math.cos(yaw * .5), sp = Math.sin(pitch * .5), cp = Math.cos(pitch * .5);
            int at = 576 + side * 16;
            packet.putFloat(at, (float)(cy * sp)); packet.putFloat(at + 4, (float)(-sy * cp));
            packet.putFloat(at + 8, (float)(sy * sp)); packet.putFloat(at + 12, (float)(cy * cp));
        }
        return true;
    }
    public void close() throws IOException {
        closed = true;
        boolean interrupted = Thread.interrupted();
        try {
            if (process != null) {
                try { process.getOutputStream().close(); } catch (IOException ignored) {}
                try {
                    if (!process.waitFor(15, TimeUnit.SECONDS)) throw new IOException("Convergence cleanup is still running");
                    if (reader != null) reader.join(1000);
                    if (process.exitValue() != 0) throw new IOException(failure == null ? "Convergence cleanup failed" : failure);
                } catch (InterruptedException error) { interrupted = true; throw new IOException("Convergence cleanup interrupted", error); }
            }
        } finally {
            if (directory != null) {
                Files.deleteIfExists(directory.resolve("bolt.ptl")); Files.deleteIfExists(directory.resolve("convergence.sh")); Files.deleteIfExists(directory);
            }
            if (interrupted) Thread.currentThread().interrupt();
        }
    }
}
