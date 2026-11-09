using System.Diagnostics;
using System.Globalization;

namespace QFTPlus;

internal static class VrssTuner
{
    internal static bool Permitted(string report) => report.Split('\n').Select(line => line.TrimEnd('\r')).ToHashSet() is var lines &&
        lines.Contains("VRSS-TUNER-READINESS-1") && lines.Contains("nvapi_vrchat_profile=readable") &&
        lines.Contains("graphics_write_entry=available") && lines.Contains("vrss_allow=enabled");

    internal static async Task Apply(string exe, string root, int mode, double area)
    {
        if (mode is < 0 or > 2 || !double.IsFinite(area) || area is < 1 or > 100) throw new IOException(Loc.Get("GameProfileInvalid"));
        if (Processes.Running("VRChat")) throw new IOException(Loc.Get("VrssCloseGame"));
        var temporary = Path.Combine(root, "temp");
        Directory.CreateDirectory(temporary);
        var report = Path.Combine(temporary, "vrss-readiness-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var check = await Processes.RunAsync(exe, ["--readiness", report], seconds: 30);
            if (check.Code != 0 || !File.Exists(report) || !Permitted(await File.ReadAllTextAsync(report)))
                throw new IOException(Loc.Get("VrssNotPermitted"));
            var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
            foreach (var arg in new[] { "--apply", mode.ToString(CultureInfo.InvariantCulture), area.ToString(CultureInfo.InvariantCulture), "default", "4" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException(Loc.Get("VrssFailed"));
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException(Loc.Format("VrssExitError", process.ExitCode));
        }
        finally { File.Delete(report); }
    }
}
