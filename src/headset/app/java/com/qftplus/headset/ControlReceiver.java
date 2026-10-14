package com.qftplus.headset;
import android.content.*;
import java.io.*;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import org.json.*;

public final class ControlReceiver extends BroadcastReceiver {
    static final int[] RATES = {0, 30, 20};

    public void onReceive(Context context, Intent intent) {
        if (intent.getBooleanExtra("avatarWanted", false)) {
            PendingResult reply = goAsync();
            new Thread(() -> {
                try {
                    File preset = AvatarAssets.prepare(context, false);
                    reply.setResultCode(0);
                    reply.setResultData(new JSONObject().put("avatarPreset", preset.getPath())
                        .put("avatarLibrary", context.getApplicationInfo().nativeLibraryDir).toString());
                } catch (IOException | JSONException error) {
                    android.util.Log.w("QFTCalibration", "Prepare avatar preview", error);
                    reply.setResultCode(2); reply.setResultData(context.getString(R.string.avatar_preview_unavailable));
                } finally { reply.finish(); }
            }, "qft-avatar-assets").start();
            return;
        }
        SharedPreferences settings = context.getSharedPreferences("settings", 0);
        int capabilities = capabilities(context);
        String problem = intent.hasExtra("rate") && java.util.Arrays.stream(RATES).noneMatch(r -> r == intent.getIntExtra("rate", -1)) ? context.getString(R.string.choose_an_update_rate_of_0_fastest_30_or_20)
            : intent.hasExtra("vergenceGain") && (!Float.isFinite(intent.getFloatExtra("vergenceGain", Float.NaN)) || intent.getFloatExtra("vergenceGain", -1) < 0 || intent.getFloatExtra("vergenceGain", 4) > 3) ? context.getString(R.string.choose_a_convergence_strength_between_0_and_300)
            : intent.hasExtra("output") && !java.util.Arrays.asList("pc", "vrchat", "raw").contains(intent.getStringExtra("output")) ? context.getString(R.string.choose_qft_on_your_pc_vrchat_or_osc_apps)
            : intent.hasExtra("oscHost") && !String.valueOf(intent.getStringExtra("oscHost")).matches("((25[0-5]|2[0-4]\\d|1?\\d?\\d)\\.){3}(25[0-5]|2[0-4]\\d|1?\\d?\\d)") ? context.getString(R.string.enter_the_receiver_s_ipv4_address_like_192_168_1)
            : intent.hasExtra("oscPort") && (intent.getIntExtra("oscPort", 0) < 1 || intent.getIntExtra("oscPort", 0) > 65535) ? context.getString(R.string.choose_a_port_from_1_to_65535)
            : intent.hasExtra("calibrate") && !java.util.Arrays.asList("face", "pupils").contains(intent.getStringExtra("calibrate")) ? context.getString(R.string.choose_face_or_pupil_calibration)
            : intent.getBooleanExtra("tongue", false) && (capabilities & 1) == 0 ? context.getString(R.string.calibrate_your_face_in_qft_headset_to_use_tongue_direction)
            : intent.getBooleanExtra("pupils", false) && (capabilities & 8) == 0 ? context.getString(R.string.calibrate_your_pupils_in_qft_headset_to_use_pupil_dilation) : null;
        byte[] adjustments = null;
        if (problem == null && intent.hasExtra("adjustments"))
            try {
                adjustments = android.util.Base64.decode(intent.getStringExtra("adjustments"), android.util.Base64.DEFAULT);
                new JSONObject(new String(adjustments, java.nio.charset.StandardCharsets.UTF_8));
            } catch (IllegalArgumentException | NullPointerException | JSONException unreadable) { problem = context.getString(R.string.the_adjustments_from_your_pc_couldn_t_be_read); }
        if (problem != null) { setResultCode(2); setResultData(problem); return; }
        if (adjustments != null)
            try { OutputAdjustments.write(TrackingService.adjustmentsFile(context), adjustments); }
            catch (IOException error) { setResultCode(2); setResultData(context.getString(R.string.could_not_save_headset_adjustments , error.getMessage())); return; }
        boolean restart = intent.hasExtra("rate") && intent.getIntExtra("rate", 0) != settings.getInt("rate", 0);
        restart |= intent.hasExtra("convergence") && intent.getBooleanExtra("convergence", true) != settings.getBoolean("convergence", true);
        SharedPreferences.Editor edit = settings.edit();
        for (String key : new String[]{"tongue", "pupils", "boot", "convergence"}) if (intent.hasExtra(key)) edit.putBoolean(key, intent.getBooleanExtra(key, false));
        if (intent.hasExtra("vergenceGain")) edit.putFloat("vergenceGain", intent.getFloatExtra("vergenceGain", 1));
        if (intent.hasExtra("rate")) edit.putInt("rate", intent.getIntExtra("rate", 0));
        restart |= intent.hasExtra("output") && !intent.getStringExtra("output").equals(TrackingService.outputMode(settings));
        if (intent.hasExtra("output")) edit.putString("output", intent.getStringExtra("output"));
        if (intent.hasExtra("oscHost") || intent.hasExtra("oscPort")) {
            restart |= intent.hasExtra("oscHost") && !intent.getStringExtra("oscHost").equals(settings.getString("oscHost", ""))
                || intent.hasExtra("oscPort") && intent.getIntExtra("oscPort", 9000) != settings.getInt("oscPort", 9000);
            if (intent.hasExtra("oscHost")) edit.putString("oscHost", intent.getStringExtra("oscHost"));
            if (intent.hasExtra("oscPort")) edit.putInt("oscPort", intent.getIntExtra("oscPort", 9000));
            edit.remove("oscService").remove("oscServiceType").remove("oscName");
        }
        boolean calibrating = TrackingService.calibration != null;
        if (calibrating && restart) { setResultCode(2); setResultData(context.getString(R.string.finish_or_leave_calibration_in_the_headset_then_change_this)); return; }
        if (intent.hasExtra("tracking")) edit.putBoolean("enabled", intent.getBooleanExtra("tracking", false));
        edit.commit();
        Intent service = new Intent(context, TrackingService.class);
        boolean on = settings.getBoolean("enabled", false);
        if (!on && intent.hasExtra("tracking") && !calibrating || on && restart) context.stopService(service);
        if (on && (intent.hasExtra("tracking") || restart))
            try { context.startForegroundService(service); }
            catch (IllegalStateException notAllowed) {
                settings.edit().putBoolean("enabled", false).commit();
                setResultCode(2); setResultData(context.getString(R.string.the_headset_didn_t_let_qft_headset_start_from_this)); return;
            }
        if (intent.hasExtra("calibrate")) {
            String refused;
            try { refused = CalibrationActivityKt.startCalibration(context, intent.getStringExtra("calibrate").equals("pupils")); }
            catch (RuntimeException notAllowed) { refused = context.getString(R.string.the_headset_didn_t_let_qft_headset_start_calibration_from); }
            if (refused != null) { setResultCode(2); setResultData(refused); return; }
        }
        try {
            JSONObject state = state(context, settings, capabilities);
            if (intent.getBooleanExtra("adjustmentsWanted", false)) {
                File file = TrackingService.adjustmentsFile(context);
                state.put("adjustmentsData", file.exists() ? android.util.Base64.encodeToString(java.nio.file.Files.readAllBytes(file.toPath()), android.util.Base64.NO_WRAP) : "");
            }
            setResultCode(0); setResultData(state.toString());
        }
        catch (IOException error) { setResultCode(2); setResultData(context.getString(R.string.could_not_read_headset_adjustments , error.getMessage())); }
        catch (JSONException error) { setResultCode(2); setResultData(error.getMessage()); }
    }

    static int capabilities(Context context) {
        try (FileInputStream input = new FileInputStream(new File(context.getFilesDir(), "model/profile.bin"))) {
            byte[] header = new byte[12];
            if (input.read(header) != 12) return 0;
            String magic = new String(header, 0, 8, "US-ASCII");
            int flags = ByteBuffer.wrap(header, 8, 4).order(ByteOrder.LITTLE_ENDIAN).getInt();
            return magic.equals("QFTHP002") ? flags : magic.equals("QFTHP001") ? flags & 8 : 0;
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
        byte[] frame = TrackingService.latest;
        boolean receiving = enabled && frame != null && android.os.SystemClock.elapsedRealtime() - TrackingService.lastFrame < 2000;
        int nativeFlags = receiving ? ByteBuffer.wrap(frame).order(ByteOrder.LITTLE_ENDIAN).getInt(288) : 0;
        return new JSONObject().put("v", 1).put("version", version).put("tracking", enabled).put("running", TrackingService.running)
            .put("receiving", receiving).put("faceAvailable", (nativeFlags & 1) != 0).put("eyesAvailable", (nativeFlags & 6) == 6)
            .put("status", enabled ? TrackingService.status : context.getString(R.string.off)).put("error", enabled || TrackingService.error == null ? "" : TrackingService.error).put("pcConnected", enabled && TrackingService.pcConnected)
            .put("settings", new JSONObject().put("enabled", enabled).put("output", TrackingService.outputMode(settings))
                .put("oscHost", settings.getString("oscHost", "")).put("oscPort", settings.getInt("oscPort", 9000)).put("oscName", settings.getString("oscName", ""))
                .put("tongue", settings.getBoolean("tongue", false)).put("pupils", settings.getBoolean("pupils", false))
                .put("convergence", settings.getBoolean("convergence", true)).put("vergenceGain", settings.getFloat("vergenceGain", 1))
                .put("boot", settings.getBoolean("boot", false)).put("rate", settings.getInt("rate", 0)))
            .put("calibrated", new JSONObject().put("face", (capabilities & 1) != 0).put("pupils", (capabilities & 8) != 0))
            .put("calibration", new JSONObject().put("running", TrackingService.calibrating).put("result", TrackingService.lastCalibration))
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
