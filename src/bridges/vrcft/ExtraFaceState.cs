using System.Text.Json;

namespace Qpro.GazeBridge;

internal sealed class ExtraFaceState
{
    internal const int TimeoutMs = 300;
    internal static readonly HashSet<string> Allowed = new(
        new[] { "CheekPuff", "CheekSuck", "BrowPinch", "BrowLowerer", "BrowInnerUp", "BrowOuterUp",
            "LipPuckerUpper", "LipPuckerLower", "LipSuckCorner", "MouthUpperDeepen",
            "MouthCornerPull", "MouthCornerSlant", "NasalDilation", "NasalConstrict" }
        .SelectMany(name => new[] { name + "Left", name + "Right" })
        .Concat(new[] { "JawBackward", "JawClench", "JawMandibleRaise", "MouthUpperLeft",
            "MouthUpperRight", "MouthLowerLeft", "MouthLowerRight" }));
    private Dictionary<string, float> _values = new();
    private long? _received;
    private sealed record Packet(int Version, bool Enabled, Dictionary<string, float> Values);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };

    internal bool Accept(byte[] packet, long now)
    {
        if (packet.Length > 4096) return false;
        try
        {
            var data = JsonSerializer.Deserialize<Packet>(packet, JsonOptions);
            if (data is not { Version: 1 } || data.Values.Any(entry => !Allowed.Contains(entry.Key) ||
                !float.IsFinite(entry.Value) || entry.Value < 0 || entry.Value > 1)) return false;
            _values = data.Enabled ? data.Values : new();
            _received = now;
            return true;
        }
        catch (JsonException)
        { return false; }
    }

    internal IReadOnlyDictionary<string, float> Current(long now) =>
        _received is long tick && now >= tick && now - tick <= TimeoutMs ? _values : Empty;
    private static readonly Dictionary<string, float> Empty = new();
}
