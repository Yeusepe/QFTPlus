package com.qftplus.headset;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import org.json.JSONObject;

final class OutputAdjustments {
    static final String[] AREAS = {"Brows", "Eyelids", "Gaze", "Pupils", "Nose", "Cheeks", "Lips", "Mouth", "Jaw", "Tongue"};
    static final String[] KEYS = {"passthrough", "match", "deadzone", "curve", "strength", "invert", "offset", "smoothing", "release", "inputMin", "inputMax", "neutral", "outputMin", "outputMax"};
    private static final int PASSTHROUGH = 0, MATCH = 1, DEADZONE = 2, CURVE = 3, STRENGTH = 4, INVERT = 5, OFFSET = 6, SMOOTHING = 7, RELEASE = 8, INPUT_MIN = 9, INPUT_MAX = 10, NEUTRAL = 11, OUTPUT_MIN = 12, OUTPUT_MAX = 13;
    static final Map<String, float[]> live = new ConcurrentHashMap<>();
    static volatile long liveTime;

    static String area(String name) {
        if (gaze(name)) return "Gaze";
        if (name.startsWith("Brow")) return "Brows";
        if (name.startsWith("Eye")) return "Eyelids";
        if (name.startsWith("Pupil")) return "Pupils";
        if (name.startsWith("Nose")) return "Nose";
        if (name.startsWith("Cheek")) return "Cheeks";
        if (name.startsWith("Lip")) return "Lips";
        if (name.startsWith("Mouth") || name.startsWith("Smile")) return "Mouth";
        if (name.startsWith("Jaw")) return "Jaw";
        return "Tongue";
    }
    private static final java.util.regex.Pattern GAZE = java.util.regex.Pattern.compile("Eye(Left|Right)?[XY]"),
        SIGNED = java.util.regex.Pattern.compile("(Jaw|Mouth(Upper|Lower)?|Tongue)X|TongueY|TongueArchY|CheekPuffSuck(Left|Right)?|BrowExpression(Left|Right)?|Smile(Frown|Sad)(Left|Right)?");
    static boolean gaze(String name) { return GAZE.matcher(name).matches(); }
    static boolean modeled(String name) {
        return name.startsWith("Brow") || name.startsWith("CheekPuff") || name.startsWith("CheekSuck") || name.startsWith("Tongue") || name.startsWith("Pupil");
    }
    static String partner(String name) {
        if (gaze(name)) return null;
        if (name.endsWith("Left")) return name.substring(0, name.length() - 4) + "Right";
        if (name.endsWith("Right")) return name.substring(0, name.length() - 5) + "Left";
        return null;
    }
    static boolean signed(String name) {
        return gaze(name) || SIGNED.matcher(name).matches();
    }
    static float neutral(String name) {
        if (name.startsWith("Pupil")) return .5f;
        if (name.startsWith("EyeOpen")) return 1;
        if (name.startsWith("EyeLid")) return .75f;
        return 0;
    }
    static float minimum(String name) { return signed(name) ? -1 : 0; }

    private final File file;
    private long seen = -1;
    private JSONObject settings = new JSONObject();
    private static final class Channel {
        final String name; final float neutral, minimum, smoothing; final String partner;
        Channel partnerChannel; double[] options; float input, filtered = Float.NaN; float[] live;
        Channel(String name) {
            this.name = name; neutral = neutral(name); minimum = minimum(name); partner = partner(name);
            smoothing = defaultSmoothing(area(name));
        }
    }
    private final Map<String, Channel> channels = new HashMap<>();
    private long timestamp;
    private Channel channel(String name) {
        Channel c = channels.get(name);
        if (c == null) { c = new Channel(name); channels.put(name, c); }
        return c;
    }

    OutputAdjustments(File file) { this.file = file; }

    static JSONObject read(File file) {
        try { return new JSONObject(new String(Files.readAllBytes(file.toPath()), StandardCharsets.UTF_8)); }
        catch (Exception missingOrInvalid) { return new JSONObject(); }
    }
    static void write(File file, JSONObject json) throws java.io.IOException, org.json.JSONException {
        write(file, json.toString(1).getBytes(StandardCharsets.UTF_8));
    }
    static void write(File file, byte[] bytes) throws java.io.IOException {
        File pending = new File(file.getPath() + ".tmp");
        Files.write(pending.toPath(), bytes);
        Files.move(pending.toPath(), file.toPath(), java.nio.file.StandardCopyOption.REPLACE_EXISTING, java.nio.file.StandardCopyOption.ATOMIC_MOVE);
    }
    private long checked;
    private void poll() {
        long clock = android.os.SystemClock.elapsedRealtime();
        if (clock - checked < 250) return;
        checked = clock;
        long time = file.lastModified();
        if (time == seen) return;
        seen = time; settings = read(file);
        for (Channel c : channels.values()) { c.options = null; c.filtered = Float.NaN; }
        passthrough = false;
        for (java.util.Iterator<String> keys = settings.keys(); keys.hasNext(); ) {
            JSONObject node = settings.optJSONObject(keys.next());
            if (node != null && node.optDouble("passthrough", 0) >= .5) passthrough = true;
        }
    }
    private boolean passthrough;
    boolean passthrough() { poll(); return passthrough; }
    private static double number(JSONObject node, String key, double fallback) {
        double value = node == null ? Double.NaN : node.optDouble(key, Double.NaN);
        return Double.isFinite(value) ? value : fallback;
    }
    private double[] resolve(Channel c) {
        if (c.options != null) return c.options;
        JSONObject own = settings.optJSONObject(c.name), area = settings.optJSONObject(area(c.name)), all = settings.optJSONObject("*");
        double[] values = new double[KEYS.length];
        for (int k = 0; k < KEYS.length; k++) values[k] = number(own, KEYS[k], number(area, KEYS[k], k < INPUT_MIN ? number(all, KEYS[k], Double.NaN) : Double.NaN));
        return c.options = values;
    }
    private static double option(double[] values, int key, double fallback) { return Double.isNaN(values[key]) ? fallback : values[key]; }
    private static double clamp(double v, double lo, double hi) { return Math.max(lo, Math.min(hi, v)); }

    void apply(Map<String, Float> values, Map<String, Float> nativeValues, long now) {
        poll();
        double dt = timestamp == 0 ? 0 : clamp((now - timestamp) / 1e9, 0, 1);
        timestamp = now;
        for (Map.Entry<String, Float> entry : values.entrySet()) {
            Channel c = channel(entry.getKey());
            Float swapped = nativeValues != null && option(resolve(c), PASSTHROUGH, 0) >= .5 ? nativeValues.get(c.name) : null;
            c.input = swapped != null ? swapped : entry.getValue();
        }
        for (Map.Entry<String, Float> entry : values.entrySet()) {
            Channel c = channels.get(entry.getKey());
            if (c.partner != null && c.partnerChannel == null) c.partnerChannel = channels.get(c.partner);
            float out = apply(c, c.input, c.partnerChannel != null && values.containsKey(c.partner) ? c.partnerChannel.input : Float.NaN, dt, resolve(c));
            entry.setValue(out);
            if (c.live == null) { c.live = new float[2]; live.put(c.name, c.live); }
            c.live[0] = c.input; c.live[1] = out;
        }
        liveTime = System.currentTimeMillis();
    }

    private float apply(Channel c, float input, float partner, double dt, double[] o) {
        float neutral = c.neutral, minimum = c.minimum, maximum = 1;
        if (!Float.isFinite(input)) input = neutral;
        double match = option(o, MATCH, 0);
        if (match >= .5 && Float.isFinite(partner))
            input = match < 1.5 ? (input + partner) / 2 : Math.abs(partner - neutral) > Math.abs(input - neutral) ? partner : input;
        double low = clamp(option(o, INPUT_MIN, minimum), minimum, maximum), high = clamp(option(o, INPUT_MAX, maximum), minimum, maximum);
        if (high - low < .0001) { low = minimum; high = maximum; }
        double center = clamp(option(o, NEUTRAL, neutral), low, high);
        double delta = clamp(input, low, high) - center;
        double travel = delta < 0 ? center - low : high - center;
        double deadzone = clamp(option(o, DEADZONE, 0), 0, maximum - minimum);
        double amount = travel > deadzone ? clamp((Math.abs(delta) - deadzone) / (travel - deadzone), 0, 1) : 0;
        amount = Math.pow(amount, clamp(option(o, CURVE, 1), .1, 5));
        double strength = clamp(option(o, STRENGTH, 1), 0, 10);
        boolean invert = option(o, INVERT, 0) >= .5;
        double offset = clamp(option(o, OFFSET, 0), minimum - maximum, maximum - minimum);
        double outputLow = clamp(option(o, OUTPUT_MIN, minimum), minimum, maximum);
        double outputHigh = clamp(option(o, OUTPUT_MAX, maximum), outputLow, maximum);
        double mapped = neutral + Math.signum(delta) * amount * (delta < 0 ? neutral - minimum : maximum - neutral) * strength;
        double rest = clamp((invert ? minimum + maximum - neutral : neutral) + offset, outputLow, outputHigh);
        float value = (float)clamp((invert ? minimum + maximum - mapped : mapped) + offset, outputLow, outputHigh);
        float previous = c.filtered;
        if (!Float.isNaN(previous) && dt > 0) {
            double smoothing = option(o, SMOOTHING, c.smoothing);
            if (Math.abs(value - rest) < Math.abs(previous - rest)) smoothing = option(o, RELEASE, smoothing);
            smoothing = clamp(smoothing, 0, 100);
            if (smoothing > 0) value = (float)(previous + alpha(cutoff(smoothing, Math.abs(value - previous) / dt), dt) * (value - previous));
        }
        value = (float)clamp(value, outputLow, outputHigh);
        c.filtered = value;
        return value;
    }
    static double cutoff(double smoothing, double speed) {
        double minimum = 1 / (2 * Math.PI * .004 * smoothing);
        return minimum * (1 + 5 * Math.min(1, speed / 5));
    }
    static float defaultSmoothing(String area) {
        return area.equals("Gaze") || area.equals("Eyelids") || area.equals("Brows") || area.equals("Cheeks") ? 20 : 0;
    }
    private static double alpha(double cutoff, double dt) { double rate = 2 * Math.PI * cutoff * dt; return rate / (rate + 1); }

    private static void check(boolean ok, Object detail) { if (!ok) throw new AssertionError(String.valueOf(detail)); }
    public static void main(String[] args) throws Exception {
        File f = File.createTempFile("adjust", ".json", new File("/data/local/tmp"));
        try { checks(f); } finally { f.delete(); }
    }
    private static void checks(File f) throws Exception {
        write(f, new JSONObject().put("*", new JSONObject().put("strength", 2)).put("Jaw", new JSONObject().put("deadzone", .2).put("inputMax", .6))
            .put("Brows", new JSONObject().put("match", 1)).put("EyeX", new JSONObject().put("invert", 1)));
        OutputAdjustments a = new OutputAdjustments(f);
        Map<String, Float> v = new HashMap<>(Map.of("JawOpen", .4f, "BrowDownLeft", .2f, "BrowDownRight", .6f, "EyeX", .3f, "MouthClosed", .25f, "PupilDilation", .75f));
        a.apply(v, null, 1);
        check(Math.abs(v.get("JawOpen") - 1) < 1e-6, v);
        check(Math.abs(v.get("BrowDownLeft") - .8f) < 1e-6 && Math.abs(v.get("BrowDownRight") - .8f) < 1e-6, v);
        check(Math.abs(v.get("EyeX") + .6f) < 1e-6, v);
        check(Math.abs(v.get("MouthClosed") - .5f) < 1e-6, v);
        check(Math.abs(v.get("PupilDilation") - 1) < 1e-6, v);
        Map<String, Float> p = new HashMap<>(Map.of("TongueOut", .9f)); write(f, new JSONObject().put("Tongue", new JSONObject().put("passthrough", 1)));
        f.setLastModified(f.lastModified() + 2000); a.checked = 0; a.apply(p, Map.of("TongueOut", .3f), 2);
        check(Math.abs(p.get("TongueOut") - .3f) < 1e-6, p);
        check(area("EyeLeftX").equals("Gaze") && area("EyeOpenLeft").equals("Eyelids") && area("SmileFrownLeft").equals("Mouth"), "areas");
        OutputAdjustments g = new OutputAdjustments(new File(f.getPath() + ".none"));
        long t = 0; float eye = 0;
        for (int i = 0; i < 60; i++) { Map<String, Float> e = new HashMap<>(Map.of("EyeX", i % 2 == 0 ? .01f : -.01f)); g.apply(e, null, t += 7_400_000); eye = e.get("EyeX"); }
        check(Math.abs(eye) < .005, eye);
        for (int i = 0; i < 6; i++) { Map<String, Float> e = new HashMap<>(Map.of("EyeX", .35f)); g.apply(e, null, t += 7_400_000); eye = e.get("EyeX"); }
        check(eye > .31, eye);
        write(f, new JSONObject().put("Gaze", new JSONObject().put("smoothing", 0)));
        f.setLastModified(f.lastModified() + 4000); a.checked = 0;
        for (float x : new float[]{.2f, -.3f, .05f}) { Map<String, Float> e = new HashMap<>(Map.of("EyeX", x)); a.apply(e, null, t += 7_400_000); check(e.get("EyeX") == x, e); }
        System.out.println("OutputAdjustments checks passed");
    }
}
