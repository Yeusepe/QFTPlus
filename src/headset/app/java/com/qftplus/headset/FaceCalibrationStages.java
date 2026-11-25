package com.qftplus.headset;

import java.io.File;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Arrays;

final class FaceCalibrationStages {
    static final int COUNT = 12;
    static final int SAMPLES = 8;
    static final long CAPTURE_FORMAT = 0x3330304c41434651L;
    static final int CAPTURE_HEADER = Long.BYTES + 2 * COUNT * Integer.BYTES;
    private static final int FRAME_BYTES = Calibration.FEATURES * Float.BYTES;
    private static final int MAX_CAPTURE = CAPTURE_HEADER + 2 * COUNT * SAMPLES * FRAME_BYTES;
    private static final int KEY_BYTES = 32;
    private static final int FACE_FLAGS = 7;

    private record Snapshot(int slot, ByteBuffer capture) {}
    private record FileStamp(long modified, long size) {
        FileStamp(File file) { this(file.lastModified(), file.length()); }
    }
    private record StagesStatus(File directory, FileStamp profile, FileStamp first, FileStamp second, int mask) {}
    private static StagesStatus status;

    private static File slot(File directory, int slot) {
        return new File(directory, "face-stages-" + slot + ".bin");
    }

    private static byte[] profile(File file) throws IOException {
        if (file.length() < 36 || file.length() > 4_000_016) throw new IOException("Invalid stage profile size");
        byte[] bytes = Files.readAllBytes(file.toPath());
        ByteBuffer header = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN);
        if (header.getLong(0) != 0x3330305048544651L || header.getInt(12) != bytes.length - 16
                || (header.getInt(8) & 4) == 0) throw new IOException("Profile has no saved face calibration");
        return bytes;
    }

    private static byte[] key(byte[] profile) {
        try {
            MessageDigest digest = MessageDigest.getInstance("SHA-256");
            // Pupil calibration changes only its flag and the final five floats.
            digest.update((byte)(profile[8] & FACE_FLAGS));
            digest.update(profile, 16, profile.length - 36);
            return digest.digest();
        } catch (NoSuchAlgorithmException impossible) {
            throw new AssertionError(impossible);
        }
    }

    private static Snapshot find(File directory, byte[] key) {
        for (int i = 0; i < 2; i++) {
            File file = slot(directory, i);
            if (file.length() < KEY_BYTES + CAPTURE_HEADER || file.length() > KEY_BYTES + MAX_CAPTURE) continue;
            try {
                byte[] bytes = Files.readAllBytes(file.toPath());
                if (bytes.length < KEY_BYTES + CAPTURE_HEADER || bytes.length > KEY_BYTES + MAX_CAPTURE
                        || !Arrays.equals(key, Arrays.copyOf(bytes, KEY_BYTES))) continue;
                ByteBuffer capture = ByteBuffer.wrap(bytes, KEY_BYTES, bytes.length - KEY_BYTES).slice().order(ByteOrder.LITTLE_ENDIAN);
                if (valid(capture.duplicate().order(ByteOrder.LITTLE_ENDIAN))) return new Snapshot(i, capture);
            } catch (IOException ignored) {}
        }
        return null;
    }

    private static boolean valid(ByteBuffer capture) {
        if (capture.getLong() != CAPTURE_FORMAT) return false;
        int[] counts = new int[COUNT];
        int frames = 0;
        for (int i = 0; i < COUNT; i++) {
            int count = capture.getInt();
            if (count != SAMPLES && (i == 0 || count != 0)) return false;
            counts[i] = count;
            frames += count;
        }
        for (int count : counts) {
            if (capture.getInt() != count) return false;
        }
        return capture.remaining() == 2 * frames * FRAME_BYTES;
    }

    static ByteBuffer read(File directory) {
        try {
            Snapshot snapshot = find(directory, key(profile(new File(directory, "profile.bin"))));
            return snapshot == null ? null : snapshot.capture();
        } catch (IOException missing) {
            return null;
        }
    }

    static synchronized int savedStages(File directory) {
        var profile = new FileStamp(new File(directory, "profile.bin"));
        var first = new FileStamp(slot(directory, 0));
        var second = new FileStamp(slot(directory, 1));
        if (status != null && directory.equals(status.directory()) && profile.equals(status.profile())
                && first.equals(status.first()) && second.equals(status.second())) return status.mask();
        ByteBuffer capture = read(directory);
        int mask = 0;
        if (capture != null) {
            capture.position(Long.BYTES);
            for (int i = 0; i < COUNT; i++) {
                if (capture.getInt() != 0) mask |= 1 << i;
            }
        }
        status = new StagesStatus(directory, profile, first, second, mask);
        return mask;
    }

    static boolean commit(File directory, boolean redo) throws IOException {
        File current = new File(directory, "profile.bin");
        File pending = new File(directory, "profile.pending");
        byte[] next = profile(pending);
        byte[] nextKey = key(next);
        byte[] previousKey = null;
        Snapshot previous = null;
        try {
            byte[] existing = profile(current);
            if (redo && (existing[8] & FACE_FLAGS & ~next[8]) != 0) return false;
            previousKey = key(existing);
            previous = find(directory, previousKey);
        } catch (IOException missing) {
            if (redo) return false;
        }
        if (redo && previous == null) return false;

        byte[] capture = Files.readAllBytes(new File(directory, "calibration.bin").toPath());
        byte[] archive = ByteBuffer.allocate(KEY_BYTES + capture.length).put(nextKey).put(capture).array();
        boolean sameProfile = Arrays.equals(previousKey, nextKey);
        int nextSlot = previous == null ? 0 : (sameProfile ? previous.slot() : 1 - previous.slot());
        File destination = slot(directory, nextSlot);
        // Write the other slot first so either profile has matching samples after an interrupted save.
        OutputAdjustments.write(destination, archive);
        Files.move(pending.toPath(), current.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
        try {
            Files.deleteIfExists(slot(directory, 1 - nextSlot).toPath());
        } catch (IOException cleanup) {
            android.util.Log.w("QFTCalibration", "Could not remove older face samples", cleanup);
        }
        return true;
    }

    static void remove(File directory) throws IOException {
        for (int i = 0; i < 2; i++) {
            File archive = slot(directory, i);
            Files.deleteIfExists(archive.toPath());
            Files.deleteIfExists(new File(archive.getPath() + ".tmp").toPath());
        }
    }
}
