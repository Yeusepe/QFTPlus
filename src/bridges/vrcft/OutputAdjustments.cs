using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Qpro.GazeBridge;

internal sealed class OutputAdjustments
{
    internal static readonly string[] Areas = ["Brows", "Eyelids", "Gaze", "Pupils", "Nose", "Cheeks", "Lips", "Mouth", "Jaw", "Tongue", "Throat and neck"];
    internal static string Area(string name) =>
        name.StartsWith("Brow") ? "Brows" : name.StartsWith("Eye") || name.StartsWith("Openness") ? "Eyelids" :
        name.StartsWith("Gaze") ? "Gaze" : name.StartsWith("Pupil") ? "Pupils" :
        name.StartsWith("Nasal") || name.StartsWith("Nose") ? "Nose" : name.StartsWith("Cheek") ? "Cheeks" :
        name.StartsWith("Lip") ? "Lips" : name.StartsWith("Mouth") ? "Mouth" : name.StartsWith("Jaw") ? "Jaw" :
        name.StartsWith("Tongue") ? "Tongue" : "Throat and neck";
    internal static bool Modeled(string name) => name.StartsWith("Gaze") || name.StartsWith("Pupil") || name.StartsWith("Tongue") || ExtraFaceState.Allowed.Contains(name);
    static readonly HashSet<string> Directions = ["JawLeft", "JawRight", "MouthUpperLeft", "MouthUpperRight", "MouthLowerLeft", "MouthLowerRight", "TongueLeft", "TongueRight", "TongueTwistLeft", "TongueTwistRight"];
    internal static string? Partner(string name) => name.StartsWith("Gaze") || Directions.Contains(name) ? null :
        name.EndsWith("Left") ? name[..^4] + "Right" : name.EndsWith("Right") ? name[..^5] + "Left" : null;

    JsonObject settings = new(), manual = new();
    readonly Dictionary<string, float> filtered = new();
    long polled;
    string? root;
    double expires;
    internal JsonObject Settings => settings;
    internal void Poll(string directory, long tick, double utcSeconds)
    {
        if (directory != root) { root = directory; polled = tick - 100; filtered.Clear(); }
        if (tick - polled < 100) return;
        polled = tick;
        var next = Read(Path.Combine(directory, "output-settings.json"));
        if (!JsonNode.DeepEquals(settings, next)) filtered.Clear();
        settings = next;
        var lease = Read(Path.Combine(directory, "manual-output.json"));
        expires = Number(lease, "expires", 0);
        manual = expires > utcSeconds && expires <= utcSeconds + 3 ? lease["values"] as JsonObject ?? new() : new();
    }
    static JsonObject Read(string path)
    {
        try
        {
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            return JsonNode.Parse(file) as JsonObject ?? new();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return new(); }
    }
    internal static double Number(JsonObject? node, string key, double fallback)
        => node?[key] is JsonValue value && value.TryGetValue<double>(out var n) && double.IsFinite(n) ? n : fallback;
    double Option(string name, string key, double fallback, bool inherit = true) =>
        Number(settings[name] as JsonObject, key, Number(settings[Area(name)] as JsonObject, key, inherit ? Number(settings["*"] as JsonObject, key, fallback) : fallback));

    internal bool Enabled(string name) => settings[name] is JsonObject || settings[Area(name)] is JsonObject || settings["*"] is JsonObject || manual.ContainsKey(name);
    internal bool Passthrough(string name) => Option(name, "passthrough", 0) >= .5;
    internal float Apply(string name, float input, float neutral, float minimum, float maximum, double dt, double utcSeconds, float? partner = null)
    {
        if (!float.IsFinite(input)) input = neutral;
        if (utcSeconds < expires && manual.ContainsKey(name))
        {
            var test = (float)Number(manual, name, neutral);
            filtered.Remove(name);
            return Math.Clamp(test, minimum, maximum);
        }
        var match = Option(name, "match", 0);
        if (match >= .5 && partner is float other && float.IsFinite(other))
            input = match < 1.5 ? (input + other) / 2 : Math.Abs(other - neutral) > Math.Abs(input - neutral) ? other : input;
        var low = Math.Clamp(Option(name, "inputMin", minimum, false), minimum, maximum);
        var high = Math.Clamp(Option(name, "inputMax", maximum, false), minimum, maximum);
        if (high - low < .0001) { low = minimum; high = maximum; }
        var center = Math.Clamp(Option(name, "neutral", neutral, false), low, high);
        var delta = Math.Clamp(input, low, high) - center;
        var travel = delta < 0 ? center - low : high - center;
        var deadzone = Math.Clamp(Option(name, "deadzone", 0), 0, maximum - minimum);

        var amount = travel > deadzone ? Math.Clamp((Math.Abs(delta) - deadzone) / (travel - deadzone), 0, 1) : 0;
        amount = Math.Pow(amount, Math.Clamp(Option(name, "curve", 1), .1, 5));
        var strength = Math.Clamp(Option(name, "strength", 1), 0, 10);
        var invert = Option(name, "invert", 0) >= .5;
        var offset = Math.Clamp(Option(name, "offset", 0), minimum - maximum, maximum - minimum);
        var outputLow = Math.Clamp(Option(name, "outputMin", minimum, false), minimum, maximum);
        var outputHigh = Math.Clamp(Option(name, "outputMax", maximum, false), outputLow, maximum);
        double Finish(double mapped) => Math.Clamp((invert ? minimum + maximum - mapped : mapped) + offset, outputLow, outputHigh);
        var value = (float)Finish(neutral + Math.Sign(delta) * amount * (delta < 0 ? neutral - minimum : maximum - neutral) * strength);
        if (filtered.TryGetValue(name, out var previous))
        {
            var smoothing = Option(name, "smoothing", 0);
            var rest = Finish(neutral);
            if (Math.Abs(value - rest) < Math.Abs(previous - rest)) smoothing = Option(name, "release", smoothing);
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
