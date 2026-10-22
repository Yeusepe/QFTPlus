package com.qftplus.headset;

import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.Map;

final class NativeFaceOsc {
    static final String[] KEYS = ("EyeLookOutRight EyeLookInRight EyeLookUpRight EyeLookDownRight " +
        "EyeLookOutLeft EyeLookInLeft EyeLookUpLeft EyeLookDownLeft EyeClosedRight EyeClosedLeft " +
        "EyeDilationRight EyeDilationLeft EyeConstrictRight EyeConstrictLeft EyeSquintRight EyeSquintLeft EyeWideRight EyeWideLeft " +
        "BrowPinchRight BrowPinchLeft BrowLowererRight BrowLowererLeft BrowInnerUpRight BrowInnerUpLeft BrowOuterUpRight BrowOuterUpLeft " +
        "NasalDilationRight NasalDilationLeft NasalConstrictRight NasalConstrictLeft CheekSquintRight CheekSquintLeft " +
        "CheekPuffRight CheekPuffLeft CheekSuckRight CheekSuckLeft JawOpen JawRight JawLeft JawForward JawBackward JawClench JawMandibleRaise MouthClosed " +
        "LipSuckUpperRight LipSuckUpperLeft LipSuckLowerRight LipSuckLowerLeft LipSuckCornerRight LipSuckCornerLeft " +
        "LipFunnelUpperRight LipFunnelUpperLeft LipFunnelLowerRight LipFunnelLowerLeft LipPuckerUpperRight LipPuckerUpperLeft LipPuckerLowerRight LipPuckerLowerLeft " +
        "MouthUpperUpRight MouthUpperUpLeft MouthUpperDeepenRight MouthUpperDeepenLeft NoseSneerRight NoseSneerLeft MouthLowerDownRight MouthLowerDownLeft " +
        "MouthUpperRight MouthUpperLeft MouthLowerRight MouthLowerLeft MouthCornerPullRight MouthCornerPullLeft MouthCornerSlantRight MouthCornerSlantLeft " +
        "MouthFrownRight MouthFrownLeft MouthStretchRight MouthStretchLeft MouthDimpleRight MouthDimpleLeft MouthRaiserUpper MouthRaiserLower " +
        "MouthPressRight MouthPressLeft MouthTightenerRight MouthTightenerLeft TongueOut TongueUp TongueDown TongueRight TongueLeft TongueRoll " +
        "TongueBendDown TongueCurlUp TongueSquish TongueFlat TongueTwistRight TongueTwistLeft SoftPalateClose ThroatSwallow NeckFlexRight NeckFlexLeft").split(" ");
    private final byte[] packet = new byte[132];

    NativeFaceOsc() {
        byte[] header = "/tracking/face/v1\0\0\0,b\0\0".getBytes(StandardCharsets.US_ASCII);
        System.arraycopy(header, 0, packet, 0, header.length);
        ByteBuffer.wrap(packet).putInt(24, 102);
    }

    byte[] encode(Map<String, Float> values) {
        Arrays.fill(packet, 28, packet.length, (byte)0);
        if (values == null) return packet;
        for (int i = 0; i < KEYS.length; i++) {
            float value = value(values, KEYS[i]);
            packet[28 + i] = (byte)(Float.isFinite(value) ? Math.round(Math.max(0, Math.min(1, value)) * 255) : 0);
        }
        return packet;
    }

    private static float get(Map<String, Float> values, String name) { return values.getOrDefault(name, 0f); }

    private static float value(Map<String, Float> v, String key) {
        Float direct = v.get(key);
        if (direct != null) return direct;
        switch (key) {
            case "EyeLookOutRight": return get(v, "EyeRightX");
            case "EyeLookInRight": return -get(v, "EyeRightX");
            case "EyeLookUpRight": return get(v, "EyeRightY");
            case "EyeLookDownRight": return -get(v, "EyeRightY");
            case "EyeLookOutLeft": return -get(v, "EyeLeftX");
            case "EyeLookInLeft": return get(v, "EyeLeftX");
            case "EyeLookUpLeft": return get(v, "EyeLeftY");
            case "EyeLookDownLeft": return -get(v, "EyeLeftY");
            case "EyeDilationRight": return pupil(v, "PupilRight") * 2 - 1;
            case "EyeDilationLeft": return pupil(v, "PupilLeft") * 2 - 1;
            case "EyeConstrictRight": return 1 - pupil(v, "PupilRight") * 2;
            case "EyeConstrictLeft": return 1 - pupil(v, "PupilLeft") * 2;
            case "JawRight": return get(v, "JawX");
            case "JawLeft": return -get(v, "JawX");
            case "JawForward": return get(v, "JawZ");
            case "JawBackward": return -get(v, "JawZ");
            case "MouthUpperRight": return get(v, "MouthUpperX");
            case "MouthUpperLeft": return -get(v, "MouthUpperX");
            case "MouthLowerRight": return get(v, "MouthLowerX");
            case "MouthLowerLeft": return -get(v, "MouthLowerX");
            case "TongueUp": return get(v, "TongueY");
            case "TongueDown": return -get(v, "TongueY");
            case "TongueRight": return get(v, "TongueX");
            case "TongueLeft": return -get(v, "TongueX");
            default: return 0;
        }
    }

    private static float pupil(Map<String, Float> values, String name) {
        float value = values.getOrDefault(name, .5f);
        return Float.isFinite(value) ? value : .5f;
    }
}
