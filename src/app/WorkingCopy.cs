using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
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
        Directory.CreateDirectory(Folder);
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
