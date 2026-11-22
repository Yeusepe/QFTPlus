namespace QFTPlus;

internal static class MicrophoneRepair
{
    internal sealed record State(bool Enabled, bool CanChange, string Message);

    internal static async Task UnmuteAsync(Session session, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new IOException(Loc.Get("MicrophoneRepairConnect"));
        var controller = Path.Combine(session.Root, "qft-microphone-control.jar");
        if (!File.Exists(controller)) throw new IOException(Loc.Get("MicrophoneSettingsMissing"));
        var adb = Adb.Exe(session.Root);
        await Adb.EnsureAsync(adb, target);
        var remote = $"/data/local/tmp/qft-microphone-repair-{Guid.NewGuid():N}.jar";
        try
        {
            if ((await Processes.RunAsync(adb, ["-s", target, "push", controller, remote], seconds: 30)).Code != 0)
                throw new IOException(Loc.Get("MicrophoneRepairNowFailed"));
            var command = "if [ \"$(id -u)\" != 0 ]; then echo 'QFT_MIC_UNMUTE root_required'; exit 1; fi\n" +
                $"CLASSPATH={Adb.Quote(remote)} app_process /system/bin MicrophoneControl --unmute";
            var result = await Processes.RunAsync(adb, Adb.Su(target, command), seconds: 15);
            Session.Log(Path.Combine(session.Root, "microphone-repair.log"), result.Text);
            var lines = result.Text.Split('\n', StringSplitOptions.TrimEntries);
            if (lines.Contains("QFT_MIC_UNMUTE root_required")) throw new IOException(Loc.Get("MicrophoneRepairRootRequired"));
            if (result.Code != 0 || !lines.Contains("QFT_MIC_UNMUTE ok"))
                throw new IOException(Loc.Get("MicrophoneRepairNowFailed"));
        }
        finally
        {
            try { await Processes.RunAsync(adb, ["-s", target, "shell", "rm", "-f", remote], seconds: 5); }
            catch (IOException error) { Session.Log(Path.Combine(session.Root, "microphone-repair.log"), error.Message); }
        }
    }

    internal static State Parse(string result) => result switch
    {
        "active" => new(true, true, "MicrophoneRepairActive"),
        "pending_on" => new(true, true, "MicrophoneRepairRestartOn"),
        "pending_off" => new(false, true, "MicrophoneRepairRestartOff"),
        "off" => new(false, true, "MicrophoneRepairOff"),
        "unsupported" => new(false, false, "MicrophoneRepairUnsupported"),
        "unsupported_enabled" => new(true, true, "MicrophoneRepairUnsupported"),
        "damaged" => new(true, true, "MicrophoneRepairDamaged"),
        "root_required" => throw new IOException(Loc.Get("MicrophoneRepairRootRequired")),
        "conflict" => throw new IOException(Loc.Get("MicrophoneRepairConflict")),
        _ => throw new IOException(Loc.Get("MicrophoneRepairFailed"))
    };

    internal static async Task<State> RunAsync(Session session, string target, bool? enable = null)
    {
        if (string.IsNullOrEmpty(target)) throw new IOException(Loc.Get("MicrophoneRepairConnect"));
        var adb = Adb.Exe(session.Root);
        await Adb.EnsureAsync(adb, target);
        using var resource = typeof(MicrophoneRepair).Assembly.GetManifestResourceStream("QFTPlus.microphone-repair.sh")!;
        using var reader = new StreamReader(resource);
        var action = enable is null ? "status" : enable.Value ? "enable" : "disable";
        var command = $"action={action}\nmodule_name={Adb.Quote(Loc.Get("MicrophoneRepairModuleName"))}\nmodule_description={Adb.Quote(Loc.Get("MicrophoneRepairAbout"))}\n" + reader.ReadToEnd().Replace("\r", "");
        var result = await Processes.RunAsync(adb, Adb.Su(target, command), seconds: 30);
        Session.Log(Path.Combine(session.Root, "microphone-repair.log"), result.Text);
        var state = result.Text.Split('\n', StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith("QFT_MIC_REPAIR ", StringComparison.Ordinal))?["QFT_MIC_REPAIR ".Length..];
        if (result.Code != 0 && state is not ("root_required" or "conflict" or "unsupported"))
            throw new IOException(Loc.Get("MicrophoneRepairFailed"));
        return Parse(state ?? "");
    }
}
