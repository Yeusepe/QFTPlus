using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using QproFaceTracking.Hub;
using Velopack.Locators;

namespace QFTPlus;

internal static class WorkingCopy
{
    internal static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QproFaceTracking");
    internal static readonly string Folder = Path.Combine(Home, "app");
    const string Sums = "SHA256SUMS.txt", Exe = "QproFaceTracking.exe";
    const StringComparison Ignore = StringComparison.OrdinalIgnoreCase;

    internal static string? Installed()
    {
        if (!VelopackLocator.IsCurrentSet || VelopackLocator.Current is not { IsPortable: false, AppContentDir: { } content, UpdateExePath: { } update }
            || !File.Exists(update) || !File.Exists(Path.Combine(content, Sums))) return null;
        return Path.GetFullPath(content).TrimEnd('\\').Equals(AppContext.BaseDirectory.TrimEnd('\\'), Ignore) ? content : null;
    }

    internal static string Prepare(string content)
    {
        if (!Directory.Exists(Folder)) Adopt();
        if (Mirror(content, Folder) && Settings() is { } settings && settings["steamvrDriver"]?.GetValue<bool>() == true)
            try { SteamVrDriver.Register(Path.Combine(Folder, "steamvr", "qftplus")); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return Folder;
    }

    internal static bool Mirror(string content, string folder)
    {
        var sums = Path.Combine(content, Sums);
        var marker = Path.Combine(folder, Sums);
        if (File.Exists(marker) && File.ReadAllBytes(marker).AsSpan().SequenceEqual(File.ReadAllBytes(sums))) return false;
        foreach (var stale in Directory.EnumerateFiles(folder, "*.replaced-*", SearchOption.AllDirectories))
            try { File.Delete(stale); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var before = File.Exists(marker) ? Read(marker) : new();
        var now = Read(sums);
        foreach (var (file, hash) in now)
        {
            if (file.Equals(Exe, Ignore)) continue;
            var target = Inside(folder, file);
            if (before.TryGetValue(file, out var was) && was == hash && File.Exists(target)) continue;
            Link(Path.Combine(content, file), target);
        }
        foreach (var (file, hash) in before)
            if (!now.ContainsKey(file) && Inside(folder, file) is var path && File.Exists(path) && Hash(path) == hash) File.Delete(path);
        if (File.Exists(Path.Combine(folder, Exe))) try { File.Delete(Path.Combine(folder, Exe)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        File.Copy(sums, marker, true);
        return true;
    }

    static void Adopt()
    {
        var apps = Path.Combine(Home, "apps");
        var old = Directory.Exists(apps) ? Directory.GetDirectories(apps, "QFT-Plus-*").Where(d => File.Exists(Path.Combine(d, "release-manifest.json")))
            .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault() : null;
        if (old is null) { Directory.CreateDirectory(Folder); return; }
        AdbServer.Stop();
        Directory.Move(old, Folder);
        if (Settings() is { } settings)
        {
            Rewrite(settings, old, Folder);
            settings["studioApp"] = Environment.ProcessPath;
            File.WriteAllText(SetupService.AutoPath + ".tmp", settings.ToJsonString(new() { WriteIndented = true }));
            File.Move(SetupService.AutoPath + ".tmp", SetupService.AutoPath, true);
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\QFTPlus", false);
        if (!Directory.EnumerateFileSystemEntries(apps).Any()) Directory.Delete(apps);
    }

    static void Rewrite(JsonNode? node, string from, string to)
    {
        if (node is JsonObject item) foreach (var key in item.Select(p => p.Key).ToList())
            if (item[key] is JsonValue value && value.TryGetValue<string>(out var text) && text.StartsWith(from, Ignore)) item[key] = to + text[from.Length..];
            else Rewrite(item[key], from, to);
        else if (node is JsonArray list) foreach (var entry in list) Rewrite(entry, from, to);
    }

    static JsonObject? Settings()
    {
        try { return File.Exists(SetupService.AutoPath) ? CalibrationSettings.ReadJson(SetupService.AutoPath) : null; }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { return null; }
    }

    static Dictionary<string, string> Read(string sums) => File.ReadAllLines(sums).Select(l => l.Split("  ", 2)).Where(p => p.Length == 2)
        .ToDictionary(p => p[1].Replace('/', '\\'), p => p[0].ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);

    static string Inside(string folder, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(folder, relative));
        return path.StartsWith(folder + Path.DirectorySeparatorChar, Ignore) ? path : throw new IOException("Unsafe path in the file list: " + relative);
    }

    static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    static void Link(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
            try { File.Delete(target); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { File.Move(target, target + ".replaced-" + Guid.NewGuid().ToString("N")[..8]); }
        if (!CreateHardLink(target, source, IntPtr.Zero)) File.Copy(source, target, true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);
}
