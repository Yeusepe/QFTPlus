using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using QFTPlus;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace Qpro.GazeBridge;

internal sealed class NativeFaceOsc : IDisposable
{
    internal static readonly string[] Keys = ("EyeLookOutRight EyeLookInRight EyeLookUpRight EyeLookDownRight " +
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
        "TongueBendDown TongueCurlUp TongueSquish TongueFlat TongueTwistRight TongueTwistLeft SoftPalateClose ThroatSwallow NeckFlexRight NeckFlexLeft").Split(' ');
    static readonly int[] ShapeIndices = Keys.Select(name => Enum.TryParse<UnifiedExpressions>(name, out var shape) ? (int)shape : -1).ToArray();
    readonly UdpClient socket = new(AddressFamily.InterNetwork);
    readonly byte[] packet = new byte[132];
    readonly bool discover;
    IPEndPoint target;
    Task<(int? Osc, int? Query)>? discovery;
    long nextDiscovery;
    bool active;

    internal NativeFaceOsc(IPEndPoint? target = null)
    {
        discover = target is null;
        this.target = target ?? new IPEndPoint(IPAddress.Loopback, VRChatOsc.DefaultPort);
    }

    internal static void Encode(UnifiedTrackingData? data, Span<byte> packet)
    {
        packet.Clear();
        "/tracking/face/v1\0\0\0,b\0\0"u8.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet[24..], 102);
        if (data is null) return;
        for (int i = 14; i < Keys.Length; i++)
            if (ShapeIndices[i] is var shape && shape >= 0 && shape < data.Shapes.Length)
                packet[28 + i] = Quantize(data.Shapes[shape].Weight);
        var eye = data.Eye;
        float pupilRange = eye._maxDilation - eye._minDilation;
        for (int side = 0; side < 2; side++)
        {
            var value = side == 0 ? eye.Right : eye.Left;
            int at = 28 + side * 4;
            packet[at] = Quantize(side == 0 ? value.Gaze.x : -value.Gaze.x);
            packet[at + 1] = Quantize(side == 0 ? -value.Gaze.x : value.Gaze.x);
            packet[at + 2] = Quantize(value.Gaze.y);
            packet[at + 3] = Quantize(-value.Gaze.y);
            packet[36 + side] = Quantize(float.IsFinite(value.Openness) ? 1 - value.Openness : 0);
            float pupil = pupilRange > 0 && float.IsFinite(pupilRange)
                ? (value.PupilDiameter_MM - eye._minDilation) / pupilRange : .5f;
            if (!float.IsFinite(pupil)) pupil = .5f;
            packet[38 + side] = Quantize(pupil * 2 - 1);
            packet[40 + side] = Quantize(1 - pupil * 2);
        }
    }

    // VRCFT's sandbox uses this finite sentinel for expressions the module does not provide.
    static byte Quantize(float value) => float.IsFinite(value) && value != (float)0xFFFFFFFF ? (byte)MathF.Floor(Math.Clamp(value, 0, 1) * 255 + .5f) : (byte)0;

    internal void Send(UnifiedTrackingData? data)
    {
        if (data is null && !active) return;
        active = data is not null;
        long now = Environment.TickCount64;
        if (discovery is { IsCompleted: true })
        {
            try { if (discovery.GetAwaiter().GetResult().Osc is int port) target = new(IPAddress.Loopback, port); }
            catch (Exception error) when (error is SocketException or InvalidOperationException) { }
            discovery = null;
        }
        if (discover && discovery is null && now >= nextDiscovery)
        {
            nextDiscovery = now + 10000;
            discovery = Task.Run(() => VRChatOsc.Find(TimeSpan.FromSeconds(.5)));
        }
        Encode(data, packet);
        try { socket.Send(packet, packet.Length, target); }
        catch (SocketException) { }
    }

    public void Dispose() { Send(null); socket.Dispose(); }
}
