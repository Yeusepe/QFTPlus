using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QFTPlus;

internal static partial class HeadsetApp
{
    internal static readonly int[] Rates = [0, 30, 20];
    internal static readonly string[] RateNames = ["Fastest", "30 per second", "20 per second"];
    internal static readonly (string Id, string Title)[] Outputs = [("pc", "QFT+ on this PC"), ("vrchat", "VRChat"), ("raw", "OSC apps")];
    internal const string Outdated = "QFT+ Headset needs an update to work with this PC. Update it, then try again.";

    internal static async Task<JsonObject> SendAsync(Session session, CancellationToken token, params (string Key, object Value)[] changes)
    {
        var adb = Adb.Exe(session.Root);
        var target = session.Config["adbTarget"]?.GetValue<string>() ?? "";
        var starts = changes.Any(c => c is ("tracking", true) or ("rate" or "convergence" or "output" or "oscHost" or "oscPort", _));
        var opens = changes.Any(c => c.Key == "calibrate");
        string[] command = ["-s", target, "shell", (starts ? "cmd deviceidle tempwhitelist -d 10000 com.qftplus.headset >/dev/null; " : "")
            + "am broadcast" + (opens ? " --allow-background-activity-starts" : "") + " --include-stopped-packages -n com.qftplus.headset/.ControlReceiver"
            + string.Concat(changes.Select(c => c.Value switch
            {
                bool on => $" --ez {c.Key} {(on ? "true" : "false")}",
                double number => $" --ef {c.Key} {number.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                string text => $" --es {c.Key} {text}",
                _ => $" --ei {c.Key} {(int)c.Value}"
            }))];
        var (code, text) = await Processes.RunAsync(adb, command, token, 10);
        if (code != 0)
        {
            await Adb.EnsureAsync(adb, target, token);
            (code, text) = await Processes.RunAsync(adb, command, token, 10);
        }
        if (code != 0) throw new IOException("Can’t reach the headset. Make sure it’s on and connected to this PC with USB or Wi-Fi.");
        return Parse(text);
    }

    internal static JsonObject Parse(string text)
    {
        var reply = Reply().Match(text);
        if (!reply.Success) throw new IOException("Can’t reach the headset. Make sure it’s on and connected to this PC with USB or Wi-Fi.");
        if (reply.Groups[1].Value != "0") throw new Refused(reply.Groups[2].Value.Length > 0 ? reply.Groups[2].Value : Outdated);
        try { return JsonNode.Parse(reply.Groups[2].Value) as JsonObject ?? throw new IOException(Outdated); }
        catch (JsonException error) { throw new IOException(Outdated, error); }
    }

    internal sealed class Refused(string message) : IOException(message);

    internal static bool Flag(JsonObject? state, string group, string key) => state?[group]?[key]?.GetValue<bool>() == true;

    internal static string Destination(JsonObject? state)
    {
        if (state?["settings"] is not JsonObject settings || settings["output"]?.GetValue<string>() is not ("vrchat" or "raw")) return "";
        var host = settings["oscHost"]?.GetValue<string>() ?? "";
        if (host.Length == 0) return "No receiver chosen yet. Choose one in QFT+ Headset, under Connections.";
        var name = settings["oscName"]?.GetValue<string>() ?? "";
        var address = $"{host}:{settings["oscPort"]?.GetValue<int>() ?? 9000}";
        return $"Sends to {(name.Length > 0 ? $"{name} ({address})" : address)}. Choose another receiver in QFT+ Headset, under Connections.";
    }

    [GeneratedRegex("result=(-?\\d+)(?:, data=\"(.*)\")?", RegexOptions.Singleline)]
    private static partial Regex Reply();
}
