package com.qftplus.headset;

import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

final class EyeProbe {
    final int[] offsets;
    final int axis;
    private final ByteBuffer data;
    private final List<long[]> segments = new ArrayList<>();
    static void require(boolean ok, String why) { if (!ok) throw new IllegalArgumentException("Unsupported eye tracking engine: " + why); }
    private long read(long at, int bytes) {
        require(at >= 0 && at <= data.capacity() - bytes, "truncated file");
        return bytes == 8 ? data.getLong((int)at) : bytes == 4 ? Integer.toUnsignedLong(data.getInt((int)at)) : Short.toUnsignedLong(data.getShort((int)at));
    }
    private long translate(long value, boolean toFile, int length, int flags) {
        long result = -1; int count = 0;
        for (long[] s : segments) {
            long from = s[toFile ? 1 : 0], to = s[toFile ? 0 : 1];
            if ((s[3] & flags) == flags && value >= from && value - from <= s[2] - length) { result = to + value - from; count++; }
        }
        require(count == 1, "address outside a unique segment"); return result;
    }
    private List<Long> matches(byte[] needle, boolean writable) {
        List<Long> found = new ArrayList<>(); byte[] raw = data.array();
        for (long[] s : segments) {
            if (writable && (s[3] & 2) == 0) continue;
            for (int p = (int)(writable ? (s[0] + 7) & ~7L : s[0]); p <= s[0] + s[2] - needle.length; p += writable ? 8 : 1) {
                int i = 0; while (i < needle.length && raw[p + i] == needle[i]) i++;
                if (i == needle.length && (!writable || p % 8 == 0)) found.add((long)p);
            }
        }
        return found;
    }
    private List<Long> pointers(long value) { return matches(ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(value).array(), true); }
    private static long unique(List<Long> values, String why) { require(values.size() == 1, why); return values.get(0); }
    private List<Integer> words(long start, int limit) {
        require(start % 4 == 0, "unaligned instructions"); List<Integer> result = new ArrayList<>();
        for (int i = 0; i < limit; i += 4) {
            translate(start + i, false, 4, 1); int word = (int)read(start + i, 4); result.add(word);
            if (word == 0xD65F03C0) return result;
        }
        throw new IllegalArgumentException("Unsupported eye tracking function boundary");
    }
    EyeProbe(byte[] image) {
        data = ByteBuffer.wrap(image).order(ByteOrder.LITTLE_ENDIAN);
        require(image.length >= 64 && Arrays.equals(Arrays.copyOf(image, 7), new byte[]{127,69,76,70,2,1,1}), "ELF64 header");
        require(read(16, 2) == 3 && read(18, 2) == 183 && read(20, 4) == 1, "AArch64 library");
        long phoff = read(32, 8), count = read(56, 2);
        require(read(54, 2) == 56 && count > 0 && count < 256, "program headers");
        for (int i = 0; i < count; i++) {
            long p = phoff + i * 56L;
            if (read(p, 4) != 1) continue;
            long offset = read(p + 8, 8), address = read(p + 16, 8), size = read(p + 32, 8);
            require(offset >= 0 && address >= 0 && size >= 0 && size <= read(p + 40, 8) && offset <= image.length - size, "segment bounds");
            segments.add(new long[]{offset, address, size, read(p + 4, 4)});
        }
        long name = unique(matches("N12eye_tracking18VisualAxisDetectorE\0".getBytes(StandardCharsets.US_ASCII), false), "detector RTTI");
        long pointer = unique(pointers(translate(name, false, 1, 0)), "typeinfo");
        long typeinfo = translate(pointer - 8, false, 24, 2);
        List<Long> tables = new ArrayList<>();
        for (long p : pointers(typeinfo)) if (p >= 8 && read(p - 8, 8) == 0) tables.add(p - 8);
        long table = unique(tables, "vtable"); translate(table, false, 40, 2);
        long address = read(table + 32, 8), process = translate(address, true, 4, 1);
        List<Integer> w = words(process, 0x400);
        int mask = 0xFFC003FF;
        require(w.size() >= 9 && w.subList(0, 4).equals(Arrays.asList(0xA9BD7BFD, 0xA90157F6, 0xA9024FF4, 0x910003FD)), "process prologue");
        require((w.get(4) & mask) == 0xF9400034 && w.get(5) == 0xAA0003F3 && (w.get(6) & mask) == 0x39400288 &&
            w.get(7) == 0x7100051F && (w.get(8) & 0xFF00001F) == 0x54000001, "EyeData access");
        int valid = (w.get(6) >>> 10) & 0xFFF;
        List<Long> calls = new ArrayList<>(), targets = new ArrayList<>();
        for (int i = 2; i < w.size(); i++) if (w.get(i - 2) == 0xAA1303E0 && w.get(i - 1) == 0xAA1403E1 && w.get(i) >>> 26 == 0x25) {
            int displacement = (w.get(i) << 6) >> 6;
            calls.add(process + i * 4L + 4); targets.add(address + i * 4L + displacement * 4L);
        }
        require(calls.size() == 2 && targets.get(0).equals(targets.get(1)), "helper calls");
        w = words(translate(targets.get(0), true, 4, 1), 0x1000);
        require(w.size() >= 32 && w.subList(0, 32).contains(0xAA0103F3) && w.subList(0, 32).contains(0x39400028 | valid << 10), "helper layout");
        List<Long> axes = new ArrayList<>();
        for (int i = 0; i < w.size() - 5; i++) {
            if ((w.get(i) & mask) != 0xFD000260 || (w.get(i + 1) & mask) != 0xBD000261) continue;
            int a = ((w.get(i) >>> 10) & 0xFFF) * 8, z = ((w.get(i + 1) >>> 10) & 0xFFF) * 4;
            if (a < 0x100 || a >= 0x1000 || z != a + 8 || valid != a + 0x5C ||
                !w.subList(i + 2, i + 5).equals(Arrays.asList(0xF9401688, 0xF85D83A9, 0xEB09011F))) continue;
            List<Integer> before = w.subList(0, i);
            if (before.contains(0xFD400260 | ((a + 0x50) / 8) << 10) && before.contains(0xBD400261 | ((a + 0x58) / 4) << 10) && before.contains(0x39000268 | (a + 12) << 10)) axes.add((long)a);
        }
        axis = (int)unique(axes, "vector write");
        offsets = new int[]{Math.toIntExact(calls.get(0)), Math.toIntExact(calls.get(1))};
    }
    String fetch() {
        return String.format(Locale.ROOT, "x=+0x%x(%%x20):x32 y=+0x%x(%%x20):x32 z=+0x%x(%%x20):x32 tag=+0x0(%%x20):x8 valid=+0x%x(%%x20):x8", axis, axis + 4, axis + 8, axis + 12);
    }
}
