using System.Text.Json.Nodes;

namespace QFTPlus;

internal sealed class GameProfiles(string root)
{
    internal sealed record Setting(string Section, string Key, string Kind, double Min = 0, double Max = 1);
    internal static readonly Setting[] Settings =
    [
        new("steamvr", "supersampleManualOverride", "bool"),
        new("steamvr", "supersampleScale", "float", 0.2, 3),
        new("driver_vrlink", "automaticBandwidth", "bool"),
        new("driver_vrlink", "targetBandwidth", "int", 25, 350),
        new("driver_vrlink", "automaticStreamFormatWidth", "bool"),
        new("driver_vrlink", "streamFormatWidth", "int", 256, 4096),
        new("steamvr", "preferredFoveatedAngle", "float", -20, 20),
        new("steamvr", "sharpening", "bool"),
        new("driver_vrlink", "useSharpening", "bool"),
        new("driver_vrlink", "sharpeningStrength", "float", 0, 1)
    ];
    readonly string path = Path.Combine(root, "config", "game-profiles.json");
    readonly string journalPath = Path.Combine(root, "config", "game-video-restore.json");
    readonly Lock gate = new();
    Task? poll;
    long nextPoll;
    string active = "";
    bool closing;
    internal string Status { get; private set; } = "";
    internal JsonObject Load() { lock (gate) return Session.Read(path); }

    internal void Save(string key, JsonObject? profile)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 1024) throw new IOException(Loc.Get("GameProfileInvalid"));
        if (profile is not null) Validate(profile);
        lock (gate)
        {
            var all = Session.Read(path);
            if (profile is null) all.Remove(key); else all[key] = profile.DeepClone();
            CalibrationSettings.WriteJson(path, all);
            Interlocked.Exchange(ref nextPoll, 0);
        }
    }

    internal static void Validate(JsonObject profile)
    {
        if (profile["settings"] is not JsonObject values) throw new IOException(Loc.Get("GameProfileInvalid"));
        foreach (var (key, value) in values)
        {
            var setting = Settings.SingleOrDefault(s => s.Key == key);
            if (setting is null) throw new IOException(Loc.Get("GameProfileInvalid"));
            ValidateValue(setting, value, limits: true);
        }
    }

    static void ValidateValue(Setting setting, JsonNode? value, bool limits)
    {
        if (value is not JsonValue v) throw new IOException(Loc.Get("GameProfileInvalid"));
        if (setting.Kind == "bool")
        {
            if (!v.TryGetValue<bool>(out _)) throw new IOException(Loc.Get("GameProfileInvalid"));
        }
        else if (!double.TryParse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) ||
            Math.Abs(n) > float.MaxValue || limits && (n < setting.Min || n > setting.Max) ||
            setting.Kind == "int" && (n != Math.Truncate(n) || n < int.MinValue || n > int.MaxValue))
            throw new IOException(Loc.Get("GameProfileInvalid"));
    }

    internal void Poll()
    {
        if (closing || poll is { IsCompleted: false } || Environment.TickCount64 < nextPoll) return;
        nextPoll = Environment.TickCount64 + 2000;
        poll = Task.Run(() =>
        {
            try
            {
                var profiles = Load();
                if (profiles.Count == 0 && !File.Exists(journalPath)) { active = ""; Status = ""; return; }
                if (!SteamVr.Running())
                {
                    if (File.Exists(journalPath)) SteamVr.RestoreVideoSettings(Restore);
                    active = ""; Status = ""; return;
                }
                var key = SteamVr.IsSteamLink ? SteamVr.CurrentGame() : "";
                var profile = profiles[key] as JsonObject;
                if (profile is not null) Validate(profile);
                var identity = key + ":" + profile?.ToJsonString();
                if (identity == active) return;
                active = "";
                SteamVr.WithVideoSettings((read, write) =>
                {
                    Restore(read, write);
                    if (profile?["settings"] is JsonObject values && values.Count > 0) Apply(values, read, write);
                });
                active = identity;
                Status = profile?["settings"] is not JsonObject { Count: > 0 } ? "" : Loc.Format("GameProfileActive", profile["name"]?.GetValue<string>() ?? key);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                active = "";
                Status = error.Message;
                nextPoll = Environment.TickCount64 + 15000;
            }
        });
    }

    // Journal each intended write first so partial failures and process crashes are recoverable.
    internal void Apply(JsonObject values, Func<Setting, JsonNode> read, Action<Setting, JsonNode> write)
    {
        var journal = new JsonObject();
        foreach (var setting in Settings.Where(s => values.ContainsKey(s.Key)))
        {
            var value = values[setting.Key]!;
            var before = read(setting);
            if (Equal(before, value)) continue;
            journal[setting.Key] = new JsonObject { ["before"] = before.DeepClone(), ["applied"] = value.DeepClone() };
            CalibrationSettings.WriteJson(journalPath, journal);
            write(setting, value);
        }
    }

    internal void Restore(Func<Setting, JsonNode> read, Action<Setting, JsonNode> write)
    {
        if (!File.Exists(journalPath)) return;
        var journal = Session.Read(journalPath);
        foreach (var key in journal.Select(pair => pair.Key).Reverse().ToArray())
        {
            var setting = Settings.SingleOrDefault(s => s.Key == key) ?? throw new IOException(Loc.Get("GameProfileInvalid"));
            var entry = journal[key] as JsonObject ?? throw new IOException(Loc.Get("GameProfileInvalid"));
            var before = entry["before"] ?? throw new IOException(Loc.Get("GameProfileInvalid"));
            var applied = entry["applied"] ?? throw new IOException(Loc.Get("GameProfileInvalid"));
            ValidateValue(setting, before, limits: false);
            ValidateValue(setting, applied, limits: true);
            if (Equal(read(setting), applied)) write(setting, before);
            journal.Remove(key);
            CalibrationSettings.WriteJson(journalPath, journal);
        }
        File.Delete(journalPath);
    }

    internal static bool Equal(JsonNode a, JsonNode b) => JsonNode.DeepEquals(a, b) ||
        double.TryParse(a.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var x) &&
        double.TryParse(b.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) < 0.00001;

    internal async Task Close()
    {
        closing = true;
        try
        {
            if (poll is not null) await poll;
            if (File.Exists(journalPath)) SteamVr.RestoreVideoSettings(Restore);
        }
        catch { closing = false; throw; }
    }
}
