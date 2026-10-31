using System.Security.Cryptography;

namespace QFTPlus;

internal static class MicrophoneSettings
{
    internal const int GateMaximum = 1048575;
    internal sealed record State(bool NoiseOff, int? GateThreshold, string Message);

    internal static State Parse(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var message = parts.FirstOrDefault() switch
        {
            "active" => "MicrophoneSettingsActive",
            "waiting" => "MicrophoneSettingsWaiting",
            "off" => "MicrophoneSettingsOff",
            "attention" => "MicrophoneSettingsFailed",
            "root_required" => throw new IOException(Loc.Get("MicrophoneRepairRootRequired")),
            "unsupported" => throw new IOException(Loc.Get("MicrophoneSettingsUnsupported")),
            "conflict" => throw new IOException(Loc.Get("MicrophoneRepairConflict")),
            _ => throw new IOException(Loc.Get("MicrophoneSettingsFailed"))
        };
        if (parts.Length != 3 || parts[1] is not ("0" or "1") || !int.TryParse(parts[2], out var threshold) || threshold < -1 || threshold > GateMaximum)
            throw new IOException(Loc.Get("MicrophoneSettingsFailed"));
        return new(parts[1] == "1", threshold < 0 ? null : threshold, message);
    }

    internal static async Task<State> RunAsync(Session session, string target, bool? noiseOff = null, int? gateThreshold = null)
    {
        if (gateThreshold is < 0 or > GateMaximum) throw new ArgumentOutOfRangeException(nameof(gateThreshold));
        if (string.IsNullOrEmpty(target)) throw new IOException(Loc.Get("MicrophoneRepairConnect"));
        var adb = Adb.Exe(session.Root);
        await Adb.EnsureAsync(adb, target);
        var hash = "";
        if (noiseOff.HasValue && (noiseOff.Value || gateThreshold.HasValue))
        {
            var controller = Path.Combine(session.Root, "qft-microphone-control.jar");
            if (!File.Exists(controller)) throw new IOException(Loc.Get("MicrophoneSettingsMissing"));
            using (var input = File.OpenRead(controller)) hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if ((await Processes.RunAsync(adb, ["-s", target, "push", controller, "/data/local/tmp/qft-microphone-control.new.jar"], seconds: 30)).Code != 0)
                throw new IOException(Loc.Get("MicrophoneSettingsFailed"));
        }
        using var resource = typeof(MicrophoneSettings).Assembly.GetManifestResourceStream("QFTPlus.microphone-settings.sh")!;
        using var reader = new StreamReader(resource);
        var command = $"action={(noiseOff.HasValue ? "apply" : "status")}\nnoise={(noiseOff == true ? 1 : 0)}\ngate_threshold={gateThreshold ?? -1}\ncontroller_sha256={hash}\n" +
            $"module_name={Adb.Quote(Loc.Get("MicrophoneSettingsModuleName"))}\nmodule_description={Adb.Quote(Loc.Get("MicrophoneSettingsAbout"))}\n" + reader.ReadToEnd().Replace("\r", "");
        var result = await Processes.RunAsync(adb, Adb.Su(target, command), seconds: 30, successPrefix: "QFT_MIC_SETTINGS ");
        Session.Log(Path.Combine(session.Root, "microphone-settings.log"), result.Text);
        var state = result.Text.Split('\n', StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("QFT_MIC_SETTINGS ", StringComparison.Ordinal))?["QFT_MIC_SETTINGS ".Length..];
        return Parse(state ?? "");
    }
}
