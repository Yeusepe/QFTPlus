using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace QFTPlus;

internal static class ControllerRepair
{
    internal static string Parse(int code, string text)
    {
        if (code == 0 && Regex.IsMatch(text, @"(?m)^\d+ CONTROLLER_PAIR_VALID [1-9]\d*\r?$"))
            return "ControllerRepairReady";
        if (text.Contains("CONTROLLER_RECOVERY_BUSY", StringComparison.Ordinal)) return "ControllerRepairBusy";
        var key = text.Contains("QFT_CONTROLLER_REPAIR root_required", StringComparison.Ordinal) ? "MicrophoneRepairRootRequired"
            : text.Contains("QFT_CONTROLLER_REPAIR unsupported", StringComparison.Ordinal) ? "ControllerRepairUnsupported" : "ControllerRepairFailed";
        throw new IOException(Loc.Get(key));
    }

    internal static async Task<string> RunAsync(Session session, string target)
    {
        if (string.IsNullOrEmpty(target)) throw new IOException(Loc.Get("ControllerRepairConnect"));
        var file = Path.Combine(session.Root, "qft-controller-recovery.jar");
        if (!File.Exists(file)) throw new IOException(Loc.Get("ControllerRepairMissing"));
        string hash;
        using (var input = File.OpenRead(file)) hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        var adb = Adb.Exe(session.Root);
        await Adb.EnsureAsync(adb, target);
        var remote = "/data/local/tmp/qft-controller-repair-" + Guid.NewGuid().ToString("N") + ".jar";
        try
        {
            if ((await Processes.RunAsync(adb, ["-s", target, "push", file, remote], seconds: 30)).Code != 0)
                throw new IOException(Loc.Get("ControllerRepairFailed"));
            var command = $"if [ \"$(id -u)\" != 0 ]; then echo 'QFT_CONTROLLER_REPAIR root_required'; exit 1; fi\n" +
                "if [ \"$(getprop ro.product.device)\" != seacliff ]; then echo 'QFT_CONTROLLER_REPAIR unsupported'; exit 1; fi\n" +
                $"[ \"$(sha256sum {Adb.Quote(remote)} | cut -d ' ' -f 1)\" = '{hash}' ] || exit 1\n" +
                $"CLASSPATH={Adb.Quote(remote)} app_process /system/bin ControllerRecovery";
            var result = await Processes.RunAsync(adb, Adb.Su(target, command), seconds: 165);
            Session.Log(Path.Combine(session.Root, "controller-repair.log"), result.Text);
            return Parse(result.Code, result.Text);
        }
        finally
        {
            try { await Processes.RunAsync(adb, ["-s", target, "shell", "rm", "-f", remote], seconds: 10); }
            catch (IOException) { }
        }
    }
}
