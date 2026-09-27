using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Qpro.GazeBridge;

internal sealed class SteamLinkState
{
    internal const int Port = 9015;
    internal const long TimeoutMs = 500;
    internal static readonly string[] ExpressionNames =
    [
        "BrowLowererL", "BrowLowererR", "CheekPuffL", "CheekPuffR",
        "CheekRaiserL", "CheekRaiserR", "CheekSuckL", "CheekSuckR",
        "ChinRaiserB", "ChinRaiserT", "DimplerL", "DimplerR",
        "EyesClosedL", "EyesClosedR", "EyesLookDownL", "EyesLookDownR",
        "EyesLookLeftL", "EyesLookLeftR", "EyesLookRightL", "EyesLookRightR",
        "EyesLookUpL", "EyesLookUpR", "InnerBrowRaiserL", "InnerBrowRaiserR",
        "JawDrop", "JawSidewaysLeft", "JawSidewaysRight", "JawThrust",
        "LidTightenerL", "LidTightenerR", "LipCornerDepressorL", "LipCornerDepressorR",
        "LipCornerPullerL", "LipCornerPullerR", "LipFunnelerLb", "LipFunnelerLt",
        "LipFunnelerRb", "LipFunnelerRt", "LipPressorL", "LipPressorR",
        "LipPuckerL", "LipPuckerR", "LipStretcherL", "LipStretcherR",
        "LipSuckLb", "LipSuckLt", "LipSuckRb", "LipSuckRt",
        "LipTightenerL", "LipTightenerR", "LipsToward", "LowerLipDepressorL",
        "LowerLipDepressorR", "MouthLeft", "MouthRight", "NoseWrinklerL",
        "NoseWrinklerR", "OuterBrowRaiserL", "OuterBrowRaiserR",
        "UpperLidRaiserL", "UpperLidRaiserR", "UpperLipRaiserL", "UpperLipRaiserR",
        "TongueTipInterdental", "TongueTipAlveolar", "TongueFrontDorsalPalate",
        "TongueMidDorsalPalate", "TongueBackDorsalVelar", "TongueOut", "TongueRetreat"
    ];
    private static readonly Dictionary<string, int> Indices = BuildIndices();
    private readonly float[] _values = new float[70];
    private readonly long[] _ticks = new long[70];
    private long _gazeTick;
    private Quaternion _gaze;

    private static Dictionary<string, int> BuildIndices()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ExpressionNames.Length; i++) result[ExpressionNames[i]] = i;
        result["ToungeTipInterdental"] = 63; result["ToungeTipAlveolar"] = 64;
        result["FrontDorsalPalate"] = 65; result["MidDorsalPalate"] = 66;
        result["BackDorsalVelar"] = 67; result["ToungeOut"] = 68; result["ToungeRetreat"] = 69;
        return result;
    }

    internal void Accept(ReadOnlySpan<byte> packet, long tick, int depth = 0)
    {
        if (packet.Length < 4 || (packet.Length & 3) != 0 || depth > 8) return;
        if (packet.StartsWith("#bundle\0"u8))
        {
            if (packet.Length < 16) return;
            int offset = 16;
            while (offset < packet.Length)
            {
                if (packet.Length - offset < 4) return;
                int length = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset, 4));
                offset += 4;
                if (length <= 0 || length > packet.Length - offset) return;
                Accept(packet.Slice(offset, length), tick, depth + 1);
                offset += length;
            }
            return;
        }
        int position = 0;
        string? address = ReadString(packet, ref position);
        string? types = ReadString(packet, ref position);
        if (address is null || types is null) return;
        if (address == "/sl/eyeTrackedGazePoint" && types == ",fff" && packet.Length - position == 12)
        {
            float x = ReadFloat(packet, position), y = ReadFloat(packet, position + 4), z = ReadFloat(packet, position + 8);
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || z >= -0.000001f) return;
            _gaze = Quaternion.CreateFromYawPitchRoll(-MathF.Atan2(x, -z), MathF.Atan2(y, -z), 0);
            _gazeTick = tick;
        }
        else if (address.StartsWith("/sl/xrfb/facew/", StringComparison.Ordinal) && types == ",f" && packet.Length - position == 4
            && Indices.TryGetValue(address[15..], out int index))
        {
            float value = ReadFloat(packet, position);
            if (!float.IsFinite(value)) return;
            _values[index] = Math.Clamp(value, 0, 1);
            _ticks[index] = tick;
        }
    }

    private static string? ReadString(ReadOnlySpan<byte> packet, ref int position)
    {
        if (position >= packet.Length) return null;
        int end = packet[position..].IndexOf((byte)0);
        if (end < 0) return null;
        string value = Encoding.ASCII.GetString(packet.Slice(position, end));
        position = (position + end + 4) & ~3;
        return position <= packet.Length ? value : null;
    }
    private static float ReadFloat(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset, 4));
    private static bool Fresh(long now, long tick) => tick > 0 && now >= tick && now - tick <= TimeoutMs;

    internal bool CopyTo(byte[] state, long now)
    {
        Array.Clear(state);
        bool face = true, any = false;
        for (int i = 0; i < _values.Length; i++)
        {
            bool fresh = Fresh(now, _ticks[i]);
            any |= fresh;
            if (i < 63) face &= fresh;
            if (fresh) BitConverter.TryWriteBytes(state.AsSpan(4 + i * 4, 4), _values[i]);
        }
        state[0] = face ? (byte)1 : (byte)0;
        if (Fresh(now, _gazeTick))
        {
            any = true;
            state[1] = state[292] = state[293] = 1;
            foreach (int offset in new[] { 296, 324 })
                MemoryMarshal.Write(state.AsSpan(offset, 16), in _gaze);
            BitConverter.TryWriteBytes(state.AsSpan(352, 4), 1f);
            BitConverter.TryWriteBytes(state.AsSpan(356, 4), 1f);
        }
        return any;
    }
}
