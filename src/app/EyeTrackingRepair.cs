using System.Text.RegularExpressions;

namespace QFTPlus;

internal static class EyeTrackingRepair
{
    internal static async Task EnableAsync(Session session, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new IOException(Loc.Get("EyeTrackingRepairConnect"));
        var adb = Adb.Exe(session.Root);
        await Adb.EnsureAsync(adb, target);
        // Use the active headset profile, not the root shell's preference scope.
        var command = "if [ \"$(id -u)\" != 0 ]; then echo 'QFT_EYE_REPAIR root_required'; exit 1; fi\n" +
            "oculuspreferences --setc et_enabled true";
        var result = await Processes.RunAsync(adb, Adb.Su(target, command), seconds: 15);
        var log = Path.Combine(session.Root, "eye-tracking-repair.log");
        Session.Log(log, result.Text);
        if (result.Text.Split('\n', StringSplitOptions.TrimEntries).Contains("QFT_EYE_REPAIR root_required"))
            throw new IOException(Loc.Get("EyeTrackingRepairRootRequired"));
        if (result.Code != 0) throw new IOException(Loc.Get("EyeTrackingRepairFailed"));
        result = await Processes.RunAsync(adb, Adb.Su(target, "oculuspreferences --getc et_enabled"), seconds: 15);
        Session.Log(log, result.Text);
        if (result.Code != 0 || !Regex.IsMatch(result.Text,
            @"^\[et_enabled\s*:\s*true\] retrieved \(user \d+\) successfully\r?$", RegexOptions.Multiline))
            throw new IOException(Loc.Get("EyeTrackingRepairFailed"));
    }
}
