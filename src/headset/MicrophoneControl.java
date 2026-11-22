import java.io.*;
import java.lang.reflect.*;
import java.nio.channels.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.TimeUnit;

public final class MicrophoneControl {
    static final Class<?> EFFECT = type("android.media.audiofx.AudioEffect");
    static final Map<Integer, Object> effects = new HashMap<>();
    static final Map<Integer, Boolean> previous = new HashMap<>();

    static Class<?> type(String name) {
        try { return Class.forName(name); }
        catch (ClassNotFoundException error) { throw new IllegalStateException(error); }
    }

    static Object call(Class<?> api, Object target, String method, Class<?>[] types, Object... args) throws Exception {
        return api.getMethod(method, types).invoke(target, args);
    }

    static Map<Integer, Boolean> sessions() throws Exception {
        Object binder = call(type("android.os.ServiceManager"), null, "getService", new Class<?>[]{String.class}, "audio");
        Object service = call(type("android.media.IAudioService$Stub"), null, "asInterface", new Class<?>[]{type("android.os.IBinder")}, binder);
        List<?> recordings = (List<?>) call(type("android.media.IAudioService"), service, "getActiveRecordingConfigurations", new Class<?>[0]);
        Map<Integer, Boolean> result = new HashMap<>();
        Class<?> config = type("android.media.AudioRecordingConfiguration");
        Class<?> device = type("android.media.AudioDeviceInfo");
        int builtIn = device.getField("TYPE_BUILTIN_MIC").getInt(null);
        Object noiseType = EFFECT.getField("EFFECT_TYPE_NS").get(null);
        for (Object recording : recordings) {
            Object input = call(config, recording, "getAudioDevice", new Class<?>[0]);
            if (input == null || (Integer) call(device, input, "getType", new Class<?>[0]) != builtIn) continue;
            int session = (Integer) call(config, recording, "getClientAudioSessionId", new Class<?>[0]);
            if (session <= 0) continue;
            boolean hasNoise = false;
            // Raw recording sessions must not gain a new preprocessing effect.
            for (Object effect : (List<?>) call(config, recording, "getClientEffects", new Class<?>[0])) {
                if (noiseType.equals(effect.getClass().getField("type").get(effect))) hasNoise = true;
            }
            result.put(session, hasNoise);
        }
        return result;
    }

    static void release(int session) {
        Object effect = effects.remove(session);
        Boolean enabled = previous.remove(session);
        if (effect == null) return;
        try {
            if ((Boolean) call(EFFECT, effect, "hasControl", new Class<?>[0]))
                call(EFFECT, effect, "setEnabled", new Class<?>[]{boolean.class}, enabled);
        } catch (Exception error) { System.err.println(error); }
        finally {
            try { call(EFFECT, effect, "release", new Class<?>[0]); }
            catch (Exception error) { System.err.println(error); }
        }
    }

    static void suppress(Set<Integer> wanted) throws Exception {
        for (int session : new HashSet<>(effects.keySet())) if (!wanted.contains(session)) release(session);
        for (int session : wanted) {
            Object effect = effects.get(session);
            if (effect == null) {
                effect = call(type("android.media.audiofx.NoiseSuppressor"), null, "create", new Class<?>[]{int.class}, session);
                if (effect == null) throw new IOException("Cannot attach noise suppression: " + session);
                effects.put(session, effect);
                previous.put(session, (Boolean) call(EFFECT, effect, "getEnabled", new Class<?>[0]));
            }
            if (!(Boolean) call(EFFECT, effect, "hasControl", new Class<?>[0])) throw new IOException("Noise suppression control unavailable");
            int status = (Integer) call(EFFECT, effect, "setEnabled", new Class<?>[]{boolean.class}, false);
            if (status != 0 || (Boolean) call(EFFECT, effect, "getEnabled", new Class<?>[0])) throw new IOException("Noise suppression stayed enabled");
        }
    }

    static String mixer(String... args) throws Exception {
        List<String> command = new ArrayList<>(Arrays.asList("/system/bin/tinymix"));
        command.addAll(Arrays.asList(args));
        Process process = new ProcessBuilder(command).redirectErrorStream(true).start();
        if (!process.waitFor(3, TimeUnit.SECONDS)) { process.destroyForcibly(); throw new IOException("Mixer timed out"); }
        ByteArrayOutputStream output = new ByteArrayOutputStream();
        byte[] buffer = new byte[512];
        int read;
        while ((read = process.getInputStream().read(buffer)) >= 0) output.write(buffer, 0, read);
        String text = new String(output.toByteArray(), StandardCharsets.UTF_8).trim();
        if (process.exitValue() != 0 || text.contains("Failed") || text.contains("Invalid")) throw new IOException(text);
        return text;
    }

    static final class Setting {
        final String name;
        Integer original, applied;
        Setting(String name) { this.name = name; }
        int read() throws Exception {
            String text = mixer(name);
            if (!text.startsWith(name + ":")) throw new IOException("Unexpected mixer value: " + text);
            String value = text.substring(name.length() + 1).trim().split("\\s+")[0];
            return value.equals("On") ? 1 : value.equals("Off") ? 0 : Integer.parseInt(value);
        }
        void apply(Integer desired) throws Exception {
            if (desired != null) {
                int value = read();
                if (original == null || !Objects.equals(value, applied)) original = value;
                if (value != desired) mixer(name, desired.toString());
                applied = desired;
                if (read() != desired) throw new IOException("Mixer change failed: " + name);
            } else if (original != null) {
                if (Objects.equals(read(), applied)) mixer(name, original.toString());
                original = applied = null;
            }
        }
    }

    static Properties options(File directory) throws IOException {
        Properties values = new Properties();
        try (InputStream input = new FileInputStream(new File(directory, "options"))) { values.load(input); }
        if (!Arrays.asList("0", "1").contains(values.getProperty("noise"))) throw new IOException("Invalid noise option");
        gateThreshold(values);
        return values;
    }

    static Integer gateThreshold(Properties values) throws IOException {
        String threshold = values.getProperty("gate_threshold");
        if (threshold == null) {
            String legacy = values.getProperty("gate");
            if ("1".equals(legacy)) return 0;
            if ("0".equals(legacy)) return null;
            throw new IOException("Invalid gate option");
        }
        try {
            if (!threshold.matches("-1|[0-9]{1,7}")) throw new NumberFormatException();
            int value = Integer.parseInt(threshold);
            if (value < -1 || value > 1048575) throw new NumberFormatException();
            return value == -1 ? null : value;
        } catch (NumberFormatException error) { throw new IOException("Invalid gate threshold", error); }
    }

    static void state(File directory, String value) throws IOException {
        Path next = new File(directory, "state.new").toPath();
        Files.write(next, (value + "\n").getBytes(StandardCharsets.UTF_8));
        Files.move(next, new File(directory, "state").toPath(), StandardCopyOption.REPLACE_EXISTING);
    }

    public static void main(String[] args) {
        int status = 0;
        try { run(args); }
        catch (Exception error) { error.printStackTrace(); status = 1; }
        // app_process keeps Binder threads alive after main returns.
        System.exit(status);
    }

    static void run(String[] args) throws Exception {
        if (args.length == 1 && args[0].equals("--unmute")) {
            Class<?> audio = type("android.media.AudioSystem");
            // Bypass the delayed framework callback that can leave the native input muted.
            int result = (Integer) call(audio, null, "muteMicrophone", new Class<?>[]{boolean.class}, false);
            if (result != 0) throw new IOException("Microphone unmute failed: " + result);
            Thread.sleep(1000);
            if ((Boolean) call(audio, null, "isMicrophoneMuted", new Class<?>[0]))
                throw new IOException("Microphone stayed muted");
            System.out.println("QFT_MIC_UNMUTE ok");
            return;
        }
        if (args.length == 1 && args[0].equals("--probe")) { System.out.println(sessions()); return; }
        if (args.length != 1) throw new IllegalArgumentException("Module directory required");
        File directory = new File(args[0]);
        Setting noise = new Setting("Mic DSP ENC"), gate = new Setting("MIC DSP Noise Gate");
        try (RandomAccessFile lockFile = new RandomAccessFile(new File(directory, "lock"), "rw");
             FileChannel channel = lockFile.getChannel(); FileLock lock = channel.tryLock()) {
            if (lock == null) return;
            Files.write(new File(directory, "pid").toPath(), call(type("android.os.Process"), null, "myPid", new Class<?>[0]).toString().getBytes(StandardCharsets.UTF_8));
            try {
                while (!new File(directory, "stop").exists() && !new File(directory, "disable").exists() && !new File(directory, "remove").exists()) {
                    try {
                        Properties values = options(directory);
                        boolean noiseOff = values.getProperty("noise").equals("1");
                        Integer threshold = gateThreshold(values);
                        Map<Integer, Boolean> active = sessions();
                        Set<Integer> filtered = new HashSet<>();
                        if (noiseOff) for (Map.Entry<Integer, Boolean> entry : active.entrySet())
                            if (entry.getValue()) filtered.add(entry.getKey());
                        suppress(filtered);
                        noise.apply(noiseOff ? 0 : null);
                        gate.apply(threshold);
                        state(directory, !noiseOff && threshold == null ? "off" : active.isEmpty() ? "waiting" : "active");
                    } catch (Exception error) {
                        System.err.println(error);
                        state(directory, "failed");
                    }
                    Thread.sleep(2000);
                }
            } finally {
                suppress(Collections.emptySet());
                try { noise.apply(null); } finally { gate.apply(null); }
                state(directory, "off");
            }
        }
    }
}
