using System.Text.Json.Nodes;

namespace Qpro.GazeBridge;

internal sealed class OutputAdjustments
{
    JsonObject settings = new(), manual = new();
    readonly Dictionary<string, float> filtered = new();
    long polled;
    string? root;
    double expires;
    internal void Poll(string directory, long tick, double utcSeconds)
    {
        if (directory != root) { root = directory; polled = 0; filtered.Clear(); }
        if (tick - polled < 100) return;
        polled = tick;
        settings = Read(Path.Combine(directory, "output-settings.json"));
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
    internal static double Number(JsonObject node, string key, double fallback)
        => node[key] is JsonValue value && value.TryGetValue<double>(out var n) && double.IsFinite(n) ? n : fallback;

    internal bool Enabled(string name) => settings[name] is JsonObject || settings["*"] is JsonObject || manual.ContainsKey(name);
    internal float Apply(string name, float input, float neutral, float minimum, float maximum, double dt, double utcSeconds)
    {
        if (!float.IsFinite(input)) input = neutral;
        if (utcSeconds < expires && manual.ContainsKey(name))
        {
            var test = (float)Number(manual, name, neutral);
            filtered.Remove(name);
            return Math.Clamp(test, minimum, maximum);
        }
        var options = settings[name] as JsonObject ?? settings["*"] as JsonObject ?? new();
        var strength = Math.Clamp(Number(options, "strength", 1), 0, 3);
        var offset = Math.Clamp(Number(options, "offset", 0), -.5, .5);
        var deadzone = Math.Clamp(Number(options, "deadzone", 0), 0, .5);
        var delta = input - neutral;
        var value = (float)Math.Clamp(neutral + Math.Sign(delta) * Math.Max(0, Math.Abs(delta) - deadzone) * strength + offset, minimum, maximum);
        var smoothing = Math.Clamp(Number(options, "smoothing", 0), 0, 100);
        if (smoothing > 0 && filtered.TryGetValue(name, out var previous))
        {
            var alpha = 1 - Math.Exp(-Math.Clamp(dt, 0, 1) / (.004 * smoothing));
            value = (float)(previous + alpha * (value - previous));
        }
        filtered[name] = value;
        return value;
    }
}
