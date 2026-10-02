using System;
using System.Collections.Generic;
using System.Linq;
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
    internal static readonly string[] SharePairs = ["BrowInnerUp", "BrowOuterUp"];
    private static readonly HashSet<string> ShareAllowed = new(SharePairs.SelectMany(name => new[] { name + "Left", name + "Right" }));
    private Dictionary<string, float> _values = new(), _shares = new();
    private long _received;
    private sealed record Packet(int Version, bool Enabled, Dictionary<string, float> Values, Dictionary<string, float>? Shares = null);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };

    internal bool Accept(ReadOnlySpan<byte> packet, long now)
    {
        if (packet.Length > 4096) return false;
        try
        {
            var data = JsonSerializer.Deserialize<Packet>(packet, JsonOptions);
            static bool Valid(KeyValuePair<string, float> entry, HashSet<string> allowed) =>
                allowed.Contains(entry.Key) && float.IsFinite(entry.Value) && entry.Value >= 0 && entry.Value <= 1;
            if (data is not { Version: 1 } || !data.Values.All(entry => Valid(entry, Allowed))
                || data.Shares is { } shares && !shares.All(entry => Valid(entry, ShareAllowed))) return false;
            _values = data.Enabled ? data.Values : new();
            _shares = data.Enabled ? data.Shares ?? new() : new();
            _received = now;
            return true;
        }
        catch (JsonException)
        { return false; }
    }

    internal IReadOnlyDictionary<string, float> Current(long now) => Packets.Fresh(_received, now, TimeoutMs) ? _values : Empty;
    internal IReadOnlyDictionary<string, float> CurrentShares(long now) => Packets.Fresh(_received, now, TimeoutMs) ? _shares : Empty;

    internal static (float Left, float Right) Split(float nativeLeft, float nativeRight, float shareLeft, float shareRight)
    {
        float height = (nativeLeft + nativeRight) / 2;
        return (Math.Min(1f, height * 2 * shareLeft), Math.Min(1f, height * 2 * shareRight));
    }
    private static readonly Dictionary<string, float> Empty = new();
}

internal static class Packets
{
    internal static bool Fresh(long tick, long now, long timeoutMs) => tick != 0 && now >= tick && now - tick <= timeoutMs;
    internal static bool Header(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> magic, byte version, int length) =>
        packet.Length == length && packet.StartsWith(magic) && packet[4] == version;
}
