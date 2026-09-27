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

    internal bool Accept(byte[] packet, long now)
    {
        if (packet.Length > 4096) return false;
        try
        {
            using var document = JsonDocument.Parse(packet);
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) return false;
            bool enabled = root.GetProperty("enabled").GetBoolean();
            var values = new Dictionary<string, float>();
            foreach (var entry in root.GetProperty("values").EnumerateObject())
            {
                float value = entry.Value.GetSingle();
                if (!Allowed.Contains(entry.Name) || !float.IsFinite(value) || value < 0 || value > 1 ||
                    !values.TryAdd(entry.Name, value)) return false;
            }
            _values = enabled ? values : new();
            _received = now;
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or OverflowException)
        { return false; }
    }

    internal IReadOnlyDictionary<string, float> Current(long now) =>
        _received is long tick && now >= tick && now - tick <= TimeoutMs ? _values : Empty;
    private static readonly Dictionary<string, float> Empty = new();
}
