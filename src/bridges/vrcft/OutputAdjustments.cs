using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qpro.GazeBridge;

internal sealed class OutputAdjustments
{
    internal static readonly string[] Areas = ["Brows", "Eyelids", "Gaze", "Pupils", "Nose", "Cheeks", "Lips", "Mouth", "Jaw", "Tongue", "Throat and neck"];
    static readonly (string Prefix, string Area)[] Prefixes = [("Brow", "Brows"), ("Eye", "Eyelids"), ("Openness", "Eyelids"), ("Gaze", "Gaze"),
        ("Pupil", "Pupils"), ("Nasal", "Nose"), ("Nose", "Nose"), ("Cheek", "Cheeks"), ("Lip", "Lips"), ("Mouth", "Mouth"), ("Jaw", "Jaw"), ("Tongue", "Tongue")];
    internal static string Area(string name) => Array.Find(Prefixes, p => name.StartsWith(p.Prefix, StringComparison.Ordinal)).Area ?? "Throat and neck";
    internal static bool Modeled(string name) => name.StartsWith("Gaze", StringComparison.Ordinal) || name.StartsWith("Pupil", StringComparison.Ordinal)
        || name.StartsWith("Tongue", StringComparison.Ordinal) || ExtraFaceState.Allowed.Contains(name);
    static readonly HashSet<string> Directions = ["JawLeft", "JawRight", "MouthUpperLeft", "MouthUpperRight", "MouthLowerLeft", "MouthLowerRight", "TongueLeft", "TongueRight", "TongueTwistLeft", "TongueTwistRight"];
    internal static string? Partner(string name) => name.StartsWith("Gaze", StringComparison.Ordinal) || Directions.Contains(name) ? null :
        name.EndsWith("Left", StringComparison.Ordinal) ? name[..^4] + "Right" : name.EndsWith("Right", StringComparison.Ordinal) ? name[..^5] + "Left" : null;

    enum Key { Passthrough, Match, Deadzone, Curve, Strength, Invert, Offset, Smoothing, Release, InputMin, InputMax, Neutral, OutputMin, OutputMax }
    static readonly string[] Keys = Array.ConvertAll(Enum.GetNames<Key>(), JsonNamingPolicy.CamelCase.ConvertName);
    static readonly DateTime Missing = DateTime.FromFileTimeUtc(0);
    JsonObject settings = new(), manual = new(), leased = new();
    readonly Dictionary<string, float> filtered = new();
    readonly Dictionary<string, (bool Configured, double[] Values)> resolved = new();
    long polled;
    string? root;
    double expires;
    DateTime settingsTime, leaseTime;
    internal JsonObject Settings => settings;
    internal void Poll(string directory, long tick, double utcSeconds)
    {
        if (directory != root) { root = directory; polled = tick - 100; filtered.Clear(); settingsTime = leaseTime = default; }
        if (tick - polled < 100) return;
        polled = tick;
        if (Read(Path.Combine(directory, "output-settings.json"), ref settingsTime) is { } next)
        {
            if (!JsonNode.DeepEquals(settings, next)) filtered.Clear();
            settings = next;
            resolved.Clear();
        }
        if (Read(Path.Combine(directory, "manual-output.json"), ref leaseTime) is { } lease)
        {
            expires = Number(lease, "expires", 0);
            leased = lease["values"] as JsonObject ?? new();
        }
        manual = expires > utcSeconds && expires <= utcSeconds + 3 ? leased : new();
    }
    internal static JsonObject? Read(string path, ref DateTime seen)
    {
        try
        {
            var time = File.GetLastWriteTimeUtc(path);
            if (time == seen) return null;
            if (time == Missing) { seen = time; return new(); }
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            var json = JsonNode.Parse(file) as JsonObject ?? new();
            seen = DateTime.UtcNow - time > TimeSpan.FromSeconds(1) ? time : default;
            return json;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    internal static double Number(JsonObject? node, string key, double fallback)
        => node?[key] is JsonValue value && value.TryGetValue<double>(out var n) && double.IsFinite(n) ? n : fallback;
    (bool Configured, double[] Values) Resolve(string name)
    {
        if (resolved.TryGetValue(name, out var options)) return options;
        JsonObject? own = settings[name] as JsonObject, area = settings[Area(name)] as JsonObject, all = settings["*"] as JsonObject;
        var values = new double[Keys.Length];
        for (int k = 0; k < Keys.Length; k++)
            values[k] = Number(own, Keys[k], Number(area, Keys[k], k < (int)Key.InputMin ? Number(all, Keys[k], double.NaN) : double.NaN));
        return resolved[name] = (own is not null || area is not null || all is not null, values);
    }
    static double Option(double[] values, Key key, double fallback) => double.IsNaN(values[(int)key]) ? fallback : values[(int)key];

    internal bool Enabled(string name) => Resolve(name).Configured || manual.ContainsKey(name);
    internal bool Passthrough(string name) => Option(Resolve(name).Values, Key.Passthrough, 0) >= .5;
    internal float Apply(string name, float input, float neutral, float minimum, float maximum, double dt, double utcSeconds, float? partner = null)
    {
        if (!float.IsFinite(input)) input = neutral;
        if (utcSeconds < expires && manual.ContainsKey(name))
        {
            var test = (float)Number(manual, name, neutral);
            filtered.Remove(name);
            return Math.Clamp(test, minimum, maximum);
        }
        var o = Resolve(name).Values;
        var match = Option(o, Key.Match, 0);
        if (match >= .5 && partner is float other && float.IsFinite(other))
            input = match < 1.5 ? (input + other) / 2 : Math.Abs(other - neutral) > Math.Abs(input - neutral) ? other : input;
        var low = Math.Clamp(Option(o, Key.InputMin, minimum), minimum, maximum);
        var high = Math.Clamp(Option(o, Key.InputMax, maximum), minimum, maximum);
        if (high - low < .0001) { low = minimum; high = maximum; }
        var center = Math.Clamp(Option(o, Key.Neutral, neutral), low, high);
        var delta = Math.Clamp(input, low, high) - center;
        var travel = delta < 0 ? center - low : high - center;
        var deadzone = Math.Clamp(Option(o, Key.Deadzone, 0), 0, maximum - minimum);

        var amount = travel > deadzone ? Math.Clamp((Math.Abs(delta) - deadzone) / (travel - deadzone), 0, 1) : 0;
        amount = Math.Pow(amount, Math.Clamp(Option(o, Key.Curve, 1), .1, 5));
        var strength = Math.Clamp(Option(o, Key.Strength, 1), 0, 10);
        var invert = Option(o, Key.Invert, 0) >= .5;
        var offset = Math.Clamp(Option(o, Key.Offset, 0), minimum - maximum, maximum - minimum);
        var outputLow = Math.Clamp(Option(o, Key.OutputMin, minimum), minimum, maximum);
        var outputHigh = Math.Clamp(Option(o, Key.OutputMax, maximum), outputLow, maximum);
        double Finish(double mapped) => Math.Clamp((invert ? minimum + maximum - mapped : mapped) + offset, outputLow, outputHigh);
        var value = (float)Finish(neutral + Math.Sign(delta) * amount * (delta < 0 ? neutral - minimum : maximum - neutral) * strength);
        if (filtered.TryGetValue(name, out var previous))
        {
            var smoothing = Option(o, Key.Smoothing, 0);
            var rest = Finish(neutral);
            if (Math.Abs(value - rest) < Math.Abs(previous - rest)) smoothing = Option(o, Key.Release, smoothing);
            smoothing = Math.Clamp(smoothing, 0, 100);
            if (smoothing > 0)
            {
                var alpha = 1 - Math.Exp(-Math.Clamp(dt, 0, 1) / (.004 * smoothing));
                value = (float)(previous + alpha * (value - previous));
            }
        }
        value = (float)Math.Clamp(value, outputLow, outputHigh);
        filtered[name] = value;
        return value;
    }
}
