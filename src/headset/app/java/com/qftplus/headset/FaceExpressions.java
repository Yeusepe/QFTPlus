package com.qftplus.headset;

import java.nio.*;
import java.util.*;

final class FaceExpressions {
    private final LinkedHashMap<String, Float> values = new LinkedHashMap<>();
    private final float[] gaze = new float[4];
    private final long[] gazeTime = new long[2], lidTimes = new long[2];
    private final float[] lids = new float[2];
    private long blinkAt;
    private final boolean nativeOnly;
    FaceExpressions(boolean nativeOnly) { this.nativeOnly = nativeOnly; }
    private static float unit(float value) { return Float.isFinite(value) ? Math.max(0, Math.min(1, value)) : 0; }
    private void put(String name, float value) { values.put(name, value); }
    private float get(String name) { return values.getOrDefault(name, 0f); }
    private void average(String name) { put(name, (get(name + "Left") + get(name + "Right")) * .5f); }

    Map<String, Float> read(byte[] payload, long now) {
        if (payload.length != 608) throw new IllegalArgumentException("Invalid tracking packet");
        ByteBuffer p = ByteBuffer.wrap(payload).order(ByteOrder.LITTLE_ENDIAN);
        int options = nativeOnly ? 0 : p.getInt(4), status = nativeOnly ? 0 : p.getInt(12), valid = p.getInt(288);
        float[] n = new float[70];
        if ((valid & 1) != 0) for (int i = 0; i < n.length; i++) n[i] = unit(p.getFloat(296 + i * 4));
        values.clear();
        if ((valid & 1) != 0) for (int eye = 0; eye < 2; eye++) {
            float lid = n[12 + eye];
            if (lid == lids[eye]) continue;
            if (lidTimes[eye] != 0 && (lid - lids[eye]) / ((now - lidTimes[eye]) / 1e9) > 3) blinkAt = now;
            lids[eye] = lid; lidTimes[eye] = now;
        }
        for (int eye = 0; eye < 2; eye++) {
            String side = eye == 0 ? "Left" : "Right";
            String[] names = {"EyeSquint", "EyeWide", "BrowPinch", "BrowLowerer", "BrowInnerUp", "BrowOuterUp",
                "CheekSquint", "CheekPuff", "CheekSuck", "NoseSneer", "LipSuckUpper", "LipSuckLower",
                "LipFunnelUpper", "LipFunnelLower", "LipPuckerUpper", "LipPuckerLower", "MouthUpperUp",
                "MouthUpperDeepen", "MouthLowerDown", "MouthCornerPull", "MouthCornerSlant", "MouthFrown",
                "MouthStretch", "MouthDimple", "MouthPress", "MouthTightener"};
            int[] source = {28,59,0,0,22,57,4,2,6,55,45,44,35,34,40,40,61,61,51,32,32,30,42,10,38,48};
            for (int i = 0; i < names.length; i++) {
                int index = source[i] + (i >= 10 && i <= 13 ? eye * 2 : eye);
                put(names[i] + side, n[index]);
            }
            put("LipSuckUpper" + side, Math.min(get("LipSuckUpper" + side), 1 - (float)Math.pow(n[61 + eye], 1.0 / 6)));
            if ((valid & 1) != 0 && (options & 9) == 9) {
                put("CheekPuff" + side, unit(p.getFloat(20 + eye * 4)));
                put("CheekSuck" + side, unit(p.getFloat(28 + eye * 4)));
            }
            if ((valid & 1) != 0 && (options & 17) == 17) {
                put("BrowLowerer" + side, unit(p.getFloat(52 + eye * 4)));
                put("BrowPinch" + side, unit(p.getFloat(60 + eye * 4)));
                put("BrowInnerUp" + side, unit((n[22] + n[23]) * unit(p.getFloat(68 + eye * 4))));
                put("BrowOuterUp" + side, unit((n[57] + n[58]) * unit(p.getFloat(76 + eye * 4))));
            }
            float open = (valid & 1) == 0 ? 1 : 1 - unit(n[12 + eye] + n[4 + eye] * n[28 + eye]);
            put("EyeOpen" + side, open); put("EyeClosed" + side, 1 - open);
            put("EyeLid" + side, open * .75f + n[59 + eye] * .25f);
            if ((valid & (2 << eye)) != 0 && now - blinkAt > 150_000_000L) {
                int at = 576 + eye * 16;
                float x = p.getFloat(at), y = p.getFloat(at + 4), z = p.getFloat(at + 8), w = p.getFloat(at + 12);
                float norm = (float)Math.sqrt(x*x + y*y + z*z + w*w);
                if (norm > 1e-6f) { x /= norm; y /= norm; z /= norm; w /= norm; }
                float gx = (float)Math.asin(Math.max(-1, Math.min(1, 2 * (x * z - w * y))));
                float gy = (float)Math.atan2(2 * (y * z + w * x), w * w - x * x - y * y + z * z);
                if (Float.isFinite(gx) && Float.isFinite(gy)) {
                    gaze[eye * 2] = Math.max(-1, Math.min(1, gx)); gaze[eye * 2 + 1] = Math.max(-1, Math.min(1, gy)); gazeTime[eye] = now;
                }
            }
            if (now - gazeTime[eye] > 1_000_000_000L) gaze[eye * 2] = gaze[eye * 2 + 1] = 0;
            put("Eye" + side + "X", gaze[eye * 2]); put("Eye" + side + "Y", gaze[eye * 2 + 1]);
            put("CheekPuffSuck" + side, get("CheekPuff" + side) - get("CheekSuck" + side));
            put("BrowDown" + side, get("BrowLowerer" + side) * .75f + get("BrowPinch" + side) * .25f);
            put("BrowUp" + side, get("BrowInnerUp" + side) * .4f + get("BrowOuterUp" + side) * .6f);
            put("BrowExpression" + side, (get("BrowInnerUp" + side) + get("BrowOuterUp" + side)) * .5f - get("BrowDown" + side));
            put("MouthSmile" + side, get("MouthCornerPull" + side) * .8f + get("MouthCornerSlant" + side) * .2f);
            put("MouthSad" + side, Math.max(get("MouthFrown" + side), get("MouthStretch" + side)));
            put("SmileFrown" + side, get("MouthSmile" + side) - get("MouthFrown" + side));
            put("SmileSad" + side, get("MouthSmile" + side) - get("MouthSad" + side));
        }
        put("EyeX", (gaze[0] + gaze[2]) * .5f); put("EyeY", (gaze[1] + gaze[3]) * .5f);
        put("JawOpen", n[24]); put("JawX", n[26] - n[25]); put("JawZ", n[27]); put("MouthClosed", n[50]);
        put("MouthUpperX", n[54] - n[53]); put("MouthLowerX", n[54] - n[53]); put("MouthX", n[54] - n[53]);
        put("MouthRaiserUpper", n[9]); put("MouthRaiserLower", n[8]);
        boolean tongue = (options & 2) != 0;
        put("TongueOut", tongue ? ((status & 4) != 0 ? unit(p.getFloat(84)) : 0) : n[68]);
        put("TongueX", tongue && (status & 4) != 0 ? Math.max(-1, Math.min(1, p.getFloat(88))) : 0);
        put("TongueY", tongue && (status & 4) != 0 ? Math.max(-1, Math.min(1, p.getFloat(92))) : 0);
        put("TongueArchY", n[69] - n[64]);
        put("PupilDilation", (status & 8) != 0 ? (unit(p.getFloat(96)) + unit(p.getFloat(100))) * .5f : .5f);
        for (String name : ("EyeOpen EyeClosed EyeLid BrowDown BrowUp BrowInnerUp BrowOuterUp BrowExpression " +
                "CheekPuffSuck CheekSuck CheekSquint NoseSneer MouthUpperUp MouthLowerDown MouthSmile MouthSad SmileFrown SmileSad " +
                "LipSuckUpper LipSuckLower LipFunnelUpper LipFunnelLower LipPuckerUpper LipPuckerLower").split(" ")) average(name);
        put("EyeWide", Math.max(n[59], n[60])); put("EyeSquint", Math.max(n[28], n[29])); put("EyesSquint", get("EyeSquint"));
        put("MouthOpen", (get("MouthUpperUp") + get("MouthLowerDown")) * .5f);
        for (String name : new String[]{"LipSuck", "LipFunnel", "LipPucker"}) put(name, (get(name + "Upper") + get(name + "Lower")) * .5f);
        return values;
    }
}
