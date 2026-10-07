package com.qftplus.headset;
import java.io.ByteArrayOutputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;

final class OscOutput implements AutoCloseable {
    static final String[] NATIVE = ("BrowLowererL BrowLowererR CheekPuffL CheekPuffR CheekRaiserL CheekRaiserR CheekSuckL CheekSuckR " +
        "ChinRaiserB ChinRaiserT DimplerL DimplerR EyesClosedL EyesClosedR EyesLookDownL EyesLookDownR EyesLookLeftL EyesLookLeftR EyesLookRightL EyesLookRightR EyesLookUpL EyesLookUpR InnerBrowRaiserL InnerBrowRaiserR " +
        "JawDrop JawSidewaysLeft JawSidewaysRight JawThrust LidTightenerL LidTightenerR LipCornerDepressorL LipCornerDepressorR LipCornerPullerL LipCornerPullerR LipFunnelerLb LipFunnelerLt LipFunnelerRb LipFunnelerRt LipPressorL LipPressorR " +
        "LipPuckerL LipPuckerR LipStretcherL LipStretcherR LipSuckLb LipSuckLt LipSuckRb LipSuckRt LipTightenerL LipTightenerR LipsToward LowerLipDepressorL LowerLipDepressorR MouthLeft MouthRight NoseWrinklerL NoseWrinklerR " +
        "OuterBrowRaiserL OuterBrowRaiserR UpperLidRaiserL UpperLidRaiserR UpperLipRaiserL UpperLipRaiserR TongueTipInterdental TongueTipAlveolar TongueFrontDorsalPalate TongueMidDorsalPalate TongueBackDorsalVelar TongueOut TongueRetreat").split(" ");
    static final String[] EXTRA = ("CheekPuffLeft CheekPuffRight CheekSuckLeft CheekSuckRight BrowInnerUpLeft BrowInnerUpRight BrowOuterUpLeft BrowOuterUpRight BrowLowererLeft BrowLowererRight BrowPinchLeft BrowPinchRight").split(" ");
    private final DatagramSocket socket = new DatagramSocket();
    private final InetAddress host;
    private final int port;
    private final boolean vrchat;
    private final FaceExpressions expressions = new FaceExpressions(false), headset = new FaceExpressions(true);
    private final OutputAdjustments adjustments;
    private long refresh;
    private static final String[] PREFIXES = {"FT/v2/", "v2/"};
    private static final int[] BITS = {1, 2, 4, 8, 16, 32, 64, 128};
    private final AvatarParameters avatar;
    private int probedAvatar = -1, reportedAvatar = -1, probes; private long probeAt, probeLowAt, probeDoneAt; private boolean probedHeard;
    private static final class Target {
        final byte[] value, negative; final byte[][] bits;
        float sent = Float.NaN; int sentSteps = -1, sentSign = -1;
        Target(byte[] value, byte[][] bits, byte[] negative) { this.value = value; this.bits = bits; this.negative = negative; }
    }
    private final java.util.HashMap<String, Target[]> targets = new java.util.HashMap<>();
    private int targetsFor = Integer.MIN_VALUE;
    private static final byte[] FLOAT = string(",f"), TRUE = string(",T"), FALSE = string(",F"), BUNDLE = string("#bundle");
    private static final byte[][] ACTIVE = {path("ExpressionTrackingActive"), path("LipTrackingActive"), path("EyeTrackingActive")};
    private static final byte[] EYES = string("/tracking/eye/LeftRightPitchYaw"), CLOSED = string("/tracking/eye/EyesClosedAmount"), FOUR = string(",ffff");
    private static final class Packet extends ByteArrayOutputStream { Packet() { super(1200); } byte[] data() { return buf; } }
    private final Packet bytes = new Packet();
    private final DataOutputStream out = new DataOutputStream(bytes);
    int messages;
    private long nextWatch;
    long readNs, applyNs, sendNs;
    double roundTrip() {
        if (avatar == null) return -1;
        synchronized (avatar) {
            double ms = avatar.echoCount > 0 ? avatar.echoTotal / 1e6 / avatar.echoCount : -1;
            avatar.echoTotal = 0; avatar.echoCount = 0; return ms;
        }
    }
    OscOutput(android.content.Context context, String host, int port, boolean vrchat, java.io.File adjustments) throws IOException {
        this.host = InetAddress.getByName(host); this.port = port; this.vrchat = vrchat; this.adjustments = new OutputAdjustments(adjustments);
        AvatarParameters learned = null;
        if (vrchat) try { learned = new AvatarParameters(context, this.host); } catch (IOException e) { android.util.Log.w("QFT", "OSCQuery unavailable", e); }
        avatar = learned;
    }
    private static byte[] string(String text) {
        byte[] raw = text.getBytes(StandardCharsets.US_ASCII), padded = new byte[(raw.length + 4) & ~3];
        System.arraycopy(raw, 0, padded, 0, raw.length); return padded;
    }
    private static byte[] path(String address) { return string("/avatar/parameters/" + address); }
    private void begin() throws IOException { bytes.reset(); out.write(BUNDLE); out.writeLong(1); }
    private void flush() {
        if (bytes.size() > 16) try { socket.send(new DatagramPacket(bytes.data(), bytes.size(), host, port)); } catch (IOException unreachable) { }
    }
    private void write(byte[] address, byte[] tag, boolean number, float v) throws IOException {
        int size = address.length + tag.length + (number ? 4 : 0);
        if (bytes.size() + size + 4 > 1200) { flush(); begin(); }
        out.writeInt(size); out.write(address); out.write(tag); if (number) out.writeFloat(v);
        messages++;
    }
    private void value(String path, float value) throws IOException {
        if (!Float.isFinite(value)) throw new IOException("Invalid tracking value");
        byte[] address = string(path);
        if (bytes.size() + address.length + 12 > 1200) { flush(); begin(); }
        out.writeInt(address.length + 8); out.write(address); out.write(string(",f")); out.writeFloat(value);
    }
    private void flag(String path, boolean on) throws IOException {
        byte[] address = string(path);
        if (bytes.size() + address.length + 8 > 1200) { flush(); begin(); }
        out.writeInt(address.length + 4); out.write(address); out.write(string(on ? ",T" : ",F"));
    }
    private void probe(java.util.Set<String> names, boolean high) throws IOException {
        begin();
        for (String name : names) for (String prefix : PREFIXES) {
            value("/avatar/parameters/" + prefix + name, high ? 1 : 0);
            for (int bit : BITS) flag("/avatar/parameters/" + prefix + name + bit, high);
            flag("/avatar/parameters/" + prefix + name + "Negative", high);
        }
        flush();
    }
    private Target[] targets(String name, boolean learned) {
        Target[] found = targets.get(name);
        if (found != null) return found;
        java.util.ArrayList<Target> list = new java.util.ArrayList<>();
        for (String prefix : PREFIXES) {
            String address = prefix + name;
            if (!learned) { list.add(new Target(path(address), new byte[0][], null)); continue; }
            Character type = avatar.present.get(address);
            int count = 0; while (count < BITS.length && avatar.present.containsKey(address + BITS[count])) count++;
            boolean number = type != null && type == 'f';
            if (!number && count == 0) continue;
            byte[][] bits = new byte[count][];
            for (int i = 0; i < count; i++) bits[i] = path(address + BITS[i]);
            list.add(new Target(number ? path(address) : null, bits, avatar.present.containsKey(address + "Negative") ? path(address + "Negative") : null));
        }
        found = list.toArray(new Target[0]); targets.put(name, found); return found;
    }
    private void expression(String name, float v, boolean all, boolean learned) throws IOException {
        if (!Float.isFinite(v)) return;
        for (Target t : targets(name, learned)) {
            if (t.value != null && (all || Float.compare(t.sent, v) != 0)) {
                write(t.value, FLOAT, true, v); t.sent = v;
                if (avatar != null && avatar.watch != null && System.nanoTime() - avatar.watchSent > 1_000_000_000L) avatar.watch = null;
                if (avatar != null && avatar.watch == null && System.nanoTime() >= nextWatch) {
                    avatar.watchBits = Float.floatToIntBits(v); avatar.watchSent = System.nanoTime(); avatar.watch = t.value; nextWatch = avatar.watchSent + 1_000_000_000L;
                }
            }
            int n = t.bits.length;
            if (n == 0) continue;
            int sign = v < 0 ? 1 : 0;
            if (t.negative != null && (all || sign != t.sentSign)) { write(t.negative, sign == 1 ? TRUE : FALSE, false, 0); t.sentSign = sign; }
            float magnitude = v < 0 && t.negative == null ? 0 : Math.abs(v);
            int steps = magnitude > 0.99999f ? (1 << n) - 1 : (int)(magnitude * (1 << n));
            int changed = all || t.sentSteps < 0 ? (1 << n) - 1 : steps ^ t.sentSteps;
            for (int i = 0; i < n; i++) if (((changed >> i) & 1) != 0) write(t.bits[i], ((steps >> i) & 1) != 0 ? TRUE : FALSE, false, 0);
            t.sentSteps = steps;
        }
    }
    private void floats(byte[] address, byte[] tag, float... v) throws IOException {
        int size = address.length + tag.length + 4 * v.length;
        if (bytes.size() + size + 4 > 1200) { flush(); begin(); }
        out.writeInt(size); out.write(address); out.write(tag); for (float f : v) out.writeFloat(f);
        messages++;
    }
    private static float degrees(float gaze) { return (float)Math.toDegrees(Math.atan(gaze)); }
    private void active(boolean face, boolean eyes) throws IOException {
        for (int i = 0; i < 3; i++) write(ACTIVE[i], (i == 2 ? eyes : face) ? TRUE : FALSE, false, 0);
    }
    void send(byte[] payload, long sequence, double ageMs) throws IOException {
        if (payload.length != 608) throw new IOException("Unsupported headset packet");
        ByteBuffer p = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
        int flags = p.getInt(4), status = p.getInt(12), nativeFlags = p.getInt(288);
        if (vrchat) {
            long now = System.nanoTime();
            boolean all = now - refresh >= 1_000_000_000L;
            long t0 = System.nanoTime();
            java.util.Map<String, Float> values = expressions.read(payload, now);
            long t1 = System.nanoTime();
            adjustments.apply(values, adjustments.passthrough() ? headset.read(payload, now) : null, now);
            readNs += t1 - t0; applyNs += System.nanoTime() - t1;
            if (avatar != null && avatar.heard && !probedHeard && probeAt == 0 && probeLowAt == 0 && probeDoneAt != 0 && now >= probeDoneAt) probedAvatar = reportedAvatar = -1;
            if (avatar != null && avatar.avatar != probedAvatar) { probedAvatar = avatar.avatar; probeAt = now + 1_500_000_000L; probeLowAt = probeDoneAt = 0; }
            if (probeAt != 0 && now >= probeAt) { probedHeard = avatar.heard; avatar.learning = true; probe(values.keySet(), true); probeAt = 0; probeLowAt = now + 350_000_000L; }
            if (probeLowAt != 0 && now >= probeLowAt) { probe(values.keySet(), false); probeLowAt = 0; probeDoneAt = now + 600_000_000L; probes++; }
            if (probeAt != 0 || probeLowAt != 0 || (probeDoneAt != 0 && now < probeDoneAt)) return;
            if (avatar != null) avatar.learning = false;
            boolean learned = avatar != null && avatar.heard && probeDoneAt != 0;
            int layout = learned ? probes : -1;
            if (layout != targetsFor) { targets.clear(); targetsFor = layout; }
            if (probeDoneAt != 0 && probedAvatar != reportedAvatar) {
                reportedAvatar = probedAvatar;
                android.util.Log.i("QFT", "VRChat avatar: " + (avatar.heard ? avatar.present.keySet().stream().filter(a -> a.contains("v2/")).count() + " face-tracking parameters" : "no reply over OSCQuery; sending defaults"));
            }
            begin();
            active((nativeFlags & 1) != 0, (nativeFlags & 7) == 7);
            long t2 = System.nanoTime();
            for (java.util.Map.Entry<String, Float> entry : values.entrySet()) expression(entry.getKey(), entry.getValue(), all, learned);
            if ((nativeFlags & 6) == 6) {
                floats(EYES, FOUR, -degrees(values.getOrDefault("EyeLeftY", 0f)), degrees(values.getOrDefault("EyeLeftX", 0f)),
                    -degrees(values.getOrDefault("EyeRightY", 0f)), degrees(values.getOrDefault("EyeRightX", 0f)));
                floats(CLOSED, FLOAT, 1 - (values.getOrDefault("EyeOpenLeft", 1f) + values.getOrDefault("EyeOpenRight", 1f)) / 2);
            }
            flush(); if (all) refresh = now;
            sendNs += System.nanoTime() - t2;
            return;
        }
        begin();
        value("/qft/status/tracking", 1); value("/qft/status/sequence", sequence & 0xffffff);
        value("/qft/status/age_ms", (float)ageMs);
        value("/qft/status/native_face", nativeFlags & 1);
        value("/qft/status/pupils", (status & 8) != 0 ? 1 : 0);
        for (int i = 0; i < 70; i++) value("/qft/native/" + NATIVE[i], p.getFloat(296 + i * 4));
        for (int i = 0; i < 12; i++) value("/qft/face/" + EXTRA[i], (flags & 1) != 0 ? p.getFloat(20 + i * 4) : 0);
        for (int i = 0; i < 4; i++) value("/qft/shares/" + EXTRA[i + 4], p.getFloat(68 + i * 4));
        value("/qft/tongue/extension", p.getFloat(84)); value("/qft/tongue/right", p.getFloat(88)); value("/qft/tongue/up", p.getFloat(92));
        for (int eye = 0; eye < 2; eye++) {
            String side = eye == 0 ? "left" : "right";
            value("/qft/pupil/" + side, p.getFloat(96 + eye * 4));
            value("/qft/pupil/" + side + "/diameter_px", p.getFloat(128 + eye * 28));
            value("/qft/eye/" + side + "/valid", (nativeFlags & (2 << eye)) != 0 ? 1 : 0);
            for (int j = 0; j < 4; j++) value("/qft/eye/" + side + "/" + "xyzw".charAt(j), p.getFloat(576 + eye * 16 + j * 4));
        }
        flush();
    }
    void idle() throws IOException {
        begin();
        if (vrchat) { active(false, false); targets.clear(); }
        else { value("/qft/status/tracking", 0); value("/qft/status/native_face", 0); value("/qft/status/pupils", 0); }
        flush();
    }
    public void close() { try { idle(); } catch (IOException ignored) {} socket.close(); if (avatar != null) avatar.close(); }
}
