package com.qftplus.headset;
import android.content.*;
import java.io.*;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import org.json.*;

public final class ControlReceiver extends BroadcastReceiver {
    static final int[] RATES = {0, 30, 20};

    public void onReceive(Context context, Intent intent) {
        SharedPreferences settings = context.getSharedPreferences("settings", 0);
        int capabilities = capabilities(context);
        String problem = intent.hasExtra("rate") && java.util.Arrays.stream(RATES).noneMatch(r -> r == intent.getIntExtra("rate", -1)) ? "Choose an update rate of 0 (fastest), 30 or 20."
            : intent.hasExtra("vergenceGain") && (!Float.isFinite(intent.getFloatExtra("vergenceGain", Float.NaN)) || intent.getFloatExtra("vergenceGain", -1) < 0 || intent.getFloatExtra("vergenceGain", 4) > 3) ? "Choose a convergence strength between 0 and 300%."
            : intent.hasExtra("output") && !java.util.Arrays.asList("pc", "vrchat", "raw").contains(intent.getStringExtra("output")) ? "Choose QFT+ on your PC, VRChat or OSC apps."
            : intent.hasExtra("oscHost") && !String.valueOf(intent.getStringExtra("oscHost")).matches("((25[0-5]|2[0-4]\\d|1?\\d?\\d)\\.){3}(25[0-5]|2[0-4]\\d|1?\\d?\\d)") ? "Enter the receiver's IPv4 address, like 192.168.1.20."
            : intent.hasExtra("oscPort") && (intent.getIntExtra("oscPort", 0) < 1 || intent.getIntExtra("oscPort", 0) > 65535) ? "Choose a port from 1 to 65535."
            : intent.hasExtra("calibrate") && !java.util.Arrays.asList("face", "pupils").contains(intent.getStringExtra("calibrate")) ? "Choose face or pupil calibration."
            : intent.getBooleanExtra("tongue", false) && (capabilities & 1) == 0 ? "Calibrate your face in QFT+ Headset to use tongue direction."
            : intent.getBooleanExtra("pupils", false) && (capabilities & 8) == 0 ? "Calibrate your pupils in QFT+ Headset to use pupil dilation." : null;
        byte[] adjustments = null;
        if (problem == null && intent.hasExtra("adjustments"))
            try {
                adjustments = android.util.Base64.decode(intent.getStringExtra("adjustments"), android.util.Base64.DEFAULT);
                new JSONObject(new String(adjustments, java.nio.charset.StandardCharsets.UTF_8));
            } catch (IllegalArgumentException | NullPointerException | JSONException unreadable) { problem = "The adjustments from your PC couldn't be read."; }
        if (problem != null) { setResultCode(2); setResultData(problem); return; }
        if (adjustments != null)
            try { OutputAdjustments.write(TrackingService.adjustmentsFile(context), adjustments); }
            catch (IOException error) { setResultCode(2); setResultData("Couldn't save the adjustments on the headset: " + error.getMessage()); return; }
        boolean restart = intent.hasExtra("rate") && intent.getIntExtra("rate", 0) != settings.getInt("rate", 0);
        restart |= intent.hasExtra("convergence") && intent.getBooleanExtra("convergence", true) != settings.getBoolean("convergence", true);
        SharedPreferences.Editor edit = settings.edit();
        for (String key : new String[]{"tongue", "pupils", "boot", "convergence"}) if (intent.hasExtra(key)) edit.putBoolean(key, intent.getBooleanExtra(key, false));
        if (intent.hasExtra("vergenceGain")) edit.putFloat("vergenceGain", intent.getFloatExtra("vergenceGain", 1));
        if (intent.hasExtra("rate")) edit.putInt("rate", intent.getIntExtra("rate", 0));
        restart |= intent.hasExtra("output") && !intent.getStringExtra("output").equals(TrackingService.outputMode(settings));
        if (intent.hasExtra("output")) edit.putString("output", intent.getStringExtra("output"));
        if (intent.hasExtra("oscHost") || intent.hasExtra("oscPort")) {
            restart = true;
            if (intent.hasExtra("oscHost")) edit.putString("oscHost", intent.getStringExtra("oscHost"));
            if (intent.hasExtra("oscPort")) edit.putInt("oscPort", intent.getIntExtra("oscPort", 9000));
            edit.remove("oscService").remove("oscServiceType").remove("oscName");
        }
        if (intent.hasExtra("tracking")) edit.putBoolean("enabled", intent.getBooleanExtra("tracking", false));
        edit.commit();
        Intent service = new Intent(context, TrackingService.class);
        boolean on = settings.getBoolean("enabled", false);
        if (!on && intent.hasExtra("tracking") || on && restart) context.stopService(service);
        if (on && (intent.hasExtra("tracking") || restart))
            try { context.startForegroundService(service); }
            catch (IllegalStateException notAllowed) {
                settings.edit().putBoolean("enabled", false).commit();
                setResultCode(2); setResultData("The headset didn't let QFT+ Headset start from this PC. Turn on tracking in QFT+ Headset."); return;
            }
        if (intent.hasExtra("calibrate"))
            try { CalibrationActivityKt.startCalibration(context, intent.getStringExtra("calibrate").equals("pupils")); }
            catch (RuntimeException notAllowed) { setResultCode(2); setResultData("The headset didn't let QFT+ Headset start calibration from this PC. Start it in QFT+ Headset."); return; }
        try {
            JSONObject state = state(context, settings, capabilities);
            if (intent.getBooleanExtra("adjustmentsWanted", false)) {
                File file = TrackingService.adjustmentsFile(context);
                state.put("adjustmentsData", file.exists() ? android.util.Base64.encodeToString(java.nio.file.Files.readAllBytes(file.toPath()), android.util.Base64.NO_WRAP) : "");
            }
            setResultCode(0); setResultData(state.toString());
        }
        catch (IOException error) { setResultCode(2); setResultData("Couldn't read the adjustments on the headset: " + error.getMessage()); }
        catch (JSONException error) { setResultCode(2); setResultData(error.getMessage()); }
    }

    static int capabilities(Context context) {
        try (FileInputStream input = new FileInputStream(new File(context.getFilesDir(), "model/profile.bin"))) {
            byte[] header = new byte[12];
            if (input.read(header) != 12 || !new String(header, 0, 8, "US-ASCII").equals("QFTHP002")) return 0;
            return ByteBuffer.wrap(header, 8, 4).order(ByteOrder.LITTLE_ENDIAN).getInt();
        } catch (IOException missing) { return 0; }
    }
    static boolean currentProfile(File profile) {
        try (FileInputStream input = new FileInputStream(profile)) {
            byte[] magic = new byte[8];
            return input.read(magic) == 8 && new String(magic, "US-ASCII").equals("QFTHP002");
        } catch (IOException missing) { return false; }
    }

    static JSONObject state(Context context, SharedPreferences settings, int capabilities) throws JSONException {
        String version;
        try { version = context.getPackageManager().getPackageInfo(context.getPackageName(), 0).versionName; }
        catch (Exception unknown) { version = ""; }
        boolean enabled = settings.getBoolean("enabled", false);
        return new JSONObject().put("v", 1).put("version", version).put("tracking", enabled)
            .put("status", enabled ? TrackingService.status : "Off").put("pcConnected", enabled && TrackingService.pcConnected)
            .put("settings", new JSONObject().put("enabled", enabled).put("output", TrackingService.outputMode(settings))
                .put("oscHost", settings.getString("oscHost", "")).put("oscPort", settings.getInt("oscPort", 9000)).put("oscName", settings.getString("oscName", ""))
                .put("tongue", settings.getBoolean("tongue", false)).put("pupils", settings.getBoolean("pupils", false))
                .put("convergence", settings.getBoolean("convergence", true)).put("vergenceGain", settings.getFloat("vergenceGain", 1))
                .put("boot", settings.getBoolean("boot", false)).put("rate", settings.getInt("rate", 0)))
            .put("calibrated", new JSONObject().put("face", (capabilities & 1) != 0).put("pupils", (capabilities & 8) != 0))
            .put("adjustments", hash(TrackingService.adjustmentsFile(context)));
    }

    static String hash(File file) {
        try {
            byte[] digest = java.security.MessageDigest.getInstance("SHA-256").digest(java.nio.file.Files.readAllBytes(file.toPath()));
            StringBuilder hex = new StringBuilder();
            for (byte b : digest) hex.append(String.format("%02x", b));
            return hex.toString();
        } catch (IOException | java.security.NoSuchAlgorithmException missing) { return ""; }
    }
}
