using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using QproFaceTracking.Hub;

namespace QFTPlus;

internal static class Uninstall
{
    const StringComparison Ignore = StringComparison.OrdinalIgnoreCase;

    internal static void Run(string root)
    {
        void Try(Action action) { try { action(); } catch (Exception error) { Trace.WriteLine("Uninstall: " + error.Message); } }
        Try(() => SteamVrDriver.Unregister());
        Try(() => RemoveModule(root));
        Try(AdbServer.Stop);
        if (File.Exists(Path.Combine(root, "SHA256SUMS.txt"))) Try(() => RemoveProgram(root));
        if (File.Exists(EverythingMarker)) Try(() => RemoveData(WorkingCopy.Home, SetupService.AutoPath));
    }

    internal static readonly string EverythingMarker = Path.Combine(WorkingCopy.Home, "uninstall-everything");

    internal static void RemoveData(string home, string settings)
    {
        string? research = null;
        try { research = CalibrationSettings.ReadJson(settings)["researchDataPath"]?.GetValue<string>(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { }
        var logs = Path.Combine(research is { Length: > 0 } ? research
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QFTPlus", "research"), "sessions");
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(settings)!, Path.GetFileName(settings) + "*")) File.Delete(file);
        foreach (var folder in new[] { home, logs }.Where(Directory.Exists))
        {
            var aside = folder + ".removing-" + Guid.NewGuid().ToString("N")[..8];
            try { Directory.Move(folder, aside); } catch (IOException) { aside = folder; }
            DeleteAfterExit(aside);
        }
    }

    static void DeleteAfterExit(string folder)
    {
        var path = folder.Replace("'", "''");
        var script = $"Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue; foreach ($attempt in 1..10) {{ try {{ " +
            $"[IO.Directory]::Delete('{path}', $true); break }} catch {{ Start-Sleep 2 }} }}";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script }) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("Windows PowerShell didn’t start.");
    }

    internal static string? Blocker()
    {
        if (SteamVrDriver.Loaded()) return "Close SteamVR, which is using the QFT+ driver.";
        var vrcft = Process.GetProcessesByName("VRCFaceTracking");
        foreach (var process in vrcft) process.Dispose();
        return vrcft.Length > 0 ? "Quit VRCFaceTracking from its system tray menu; it has the QFT+ module loaded." : null;
    }

    static void Delete(string file) { if (File.Exists(file)) File.Delete(file); }

    static void RemoveModule(string root)
    {
        var customLibs = Path.Combine(Path.GetDirectoryName(SetupService.AutoPath)!, "CustomLibs");
        Delete(Path.Combine(customLibs, "000-Qpro.IndependentGaze.dll"));
        var backups = Path.Combine(root, "backups");
        if (!Directory.Exists(backups)) return;
        foreach (var backup in Directory.GetDirectories(backups, "vrcft-*").OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var name = Path.GetFileName(backup);
            if (name.Length != 75 || name[42] != '-' || !Guid.TryParseExact(name[6..42], "D", out _)) continue;
            var target = Path.Combine(customLibs, name[6..42]);
            if (!Path.Exists(target)) Directory.Move(backup, target);
        }
    }

    internal static void RemoveProgram(string root)
    {
        void Gone(Action delete) { try { delete(); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } }
        var sums = Path.Combine(root, "SHA256SUMS.txt");
        foreach (var line in File.ReadAllLines(sums))
        {
            var parts = line.Split("  ", 2);
            if (parts.Length != 2) continue;
            var file = Path.GetFullPath(Path.Combine(root, parts[1]));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, Ignore) || !File.Exists(file)) continue;
            Gone(() =>
            {
                using (var stream = File.OpenRead(file))
                    if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(parts[0], Ignore)) return;
                File.Delete(file);
            });
        }
        File.Delete(sums);
        var links = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var runtime = Path.Combine(root, "runtime");
        if (Directory.Exists(runtime)) Gone(() => Directory.Delete(runtime, true));
        foreach (var cache in Directory.GetDirectories(root, "__pycache__", links)) Gone(() => Directory.Delete(cache, true));
        foreach (var file in Directory.GetFiles(root, "*", links))
            if (Path.GetFileName(file) is var name && (name.StartsWith(".qpro-", Ignore) || name.EndsWith(".pyc", Ignore) || name.StartsWith("autostart-status.json", Ignore) || name.Contains(".replaced-", Ignore)))
                Gone(() => File.Delete(file));
        foreach (var folder in Directory.GetDirectories(root, "*", links).OrderByDescending(folder => folder.Length))
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }
}
