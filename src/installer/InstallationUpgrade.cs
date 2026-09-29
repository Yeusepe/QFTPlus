using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QproFaceTracking.Setup;

internal sealed class InstallationUpgrade
{
    readonly string apps, destination, configPath;
    readonly byte[]? originalConfig;
    readonly JsonObject config;
    readonly List<string> oldRoots = new();
    readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> directories = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> archives = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> migratedHashes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> originalHashes = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Source, string Target, string Hash)> copies = new();
    readonly Dictionary<string, string[]> inventories = new(StringComparer.OrdinalIgnoreCase);
    byte[]? committedConfig;

    internal InstallationUpgrade(string appsDirectory, string installed, string settings)
    {
        apps = Full(appsDirectory); destination = Full(installed); configPath = settings;
        if (!ChildOf(destination, apps) || Path.GetDirectoryName(destination) != apps) throw new IOException("Unsafe installation folder.");
        NoLinks(apps); NoLinks(destination);
        originalConfig = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        config = originalConfig is null ? new() : ReadObject(settings);
    }

    static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    static bool ChildOf(string path, string parent) => Full(path).StartsWith(Full(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
    static JsonObject ReadObject(string file) => JsonNode.Parse(File.ReadAllText(file))?.AsObject() ?? throw new IOException("Settings are empty: " + file);
    static void NoLinks(string path)
    {
        for (var entry = new DirectoryInfo(path); entry is not null; entry = entry.Parent)
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Move QFT+ out of linked folders before updating.");
    }
    static string[] Files(string root)
    {
        var files = new List<string>();
        void Walk(string folder)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("An installation contains a linked file or folder. Old installations were kept.");
                if ((attributes & FileAttributes.Directory) != 0) Walk(entry); else files.Add(entry);
            }
        }
        NoLinks(root); Walk(root); return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    static Dictionary<string, string> PackageFiles(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(Path.Combine(root, "SHA256SUMS.txt")))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split("  ", 2);
            if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit)) throw new IOException("An old installation has a damaged file list. Old installations were kept.");
            var relative = parts[1].Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p is ".." or "." || p.Contains(':')) || !result.TryAdd(relative, parts[0]))
                throw new IOException("An old installation has an unsafe file list. Old installations were kept.");
        }
        if (!result.ContainsKey("QproFaceTracking.exe") || !result.ContainsKey("release-manifest.json")) throw new IOException("An old installation cannot be identified safely.");
        return result;
    }
    static bool Recreated(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar);
        return parts[0].Equals("runtime", StringComparison.OrdinalIgnoreCase) || parts.Any(p => p.Equals("__pycache__", StringComparison.OrdinalIgnoreCase))
            || parts[^1].EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) || parts[^1].StartsWith(".qpro-", StringComparison.OrdinalIgnoreCase)
            || relative is "autostart-status.json" or "autostart-status.json.tmp";
    }

    internal void Prepare()
    {
        foreach (var folder in Directory.GetDirectories(apps))
        {
            if (Full(folder).Equals(destination, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(folder).StartsWith("QFT-Plus-", StringComparison.OrdinalIgnoreCase)) continue;
            NoLinks(folder);
            if (!File.Exists(Path.Combine(folder, "release-manifest.json")) || !File.Exists(Path.Combine(folder, "QproFaceTracking.exe")) || !File.Exists(Path.Combine(folder, "SHA256SUMS.txt"))) continue;
            var manifest = ReadObject(Path.Combine(folder, "release-manifest.json"));
            if (manifest["name"]?.GetValue<string>() != "QFT+" || string.IsNullOrWhiteSpace(manifest["version"]?.GetValue<string>())) continue;
            oldRoots.Add(Full(folder));
        }
        var active = config["root"]?.GetValue<string>();
        active = active is null ? oldRoots.OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault() : Full(active);
        foreach (var root in oldRoots)
        {
            archives[root] = Path.Combine(destination, "previous-data", Path.GetFileName(root) + "-" + Guid.NewGuid().ToString("N")[..8]);
            directories[root] = root.Equals(active, StringComparison.OrdinalIgnoreCase) ? destination : archives[root];
        }
        if (active is not null && Directory.Exists(active) && !directories.ContainsKey(active) && !active.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            directories[active] = destination;
            archives[active] = Path.Combine(destination, "previous-data", "portable-" + Guid.NewGuid().ToString("N")[..8]);
        }
        EnsureStopped();
        foreach (var (root, targetRoot) in directories)
        {
            var managed = oldRoots.Contains(root, StringComparer.OrdinalIgnoreCase);
            var package = managed ? PackageFiles(root) : new Dictionary<string, string>();
            var files = managed ? Files(root) : PortableFiles(root);
            if (managed) inventories[root] = files;
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative == "SHA256SUMS.txt" || Recreated(relative)) continue;
                var target = Path.Combine(targetRoot, relative);
                var original = Hash(file);
                originalHashes[file] = original;
                if (package.TryGetValue(relative, out var shipped) && original.Equals(shipped, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(Path.Combine(destination, relative))) { paths[file] = Path.Combine(destination, relative); continue; }
                    if (!References(config, file)) continue;
                }
                else if (File.Exists(target) && !relative.StartsWith("calibration" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    target = Path.Combine(archives[root], relative);
                }
                Copy(file, target, original); paths[file] = target;
            }
        }
        string Remap(string text)
        {
            if (!Path.IsPathFullyQualified(text)) return text;
            var path = Full(text);
            if (paths.TryGetValue(path, out var replacement)) return replacement;
            foreach (var (root, target) in directories)
                if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return target;
                else if (ChildOf(path, root))
                {
                    var moved = Path.Combine(target, Path.GetRelativePath(root, path));
                    if (File.Exists(moved) || Directory.Exists(moved)) return moved;
                    if (oldRoots.Contains(root, StringComparer.OrdinalIgnoreCase)) throw new IOException("A saved path could not be migrated: " + text);
                }
            return text;
        }
        var python = config["runtimePython"]?.GetValue<string>();
        config["runtimePython"] = python is not null && File.Exists(python) && !directories.Keys.Any(root => ChildOf(python, root)) ? python : Path.Combine(destination, "runtime", "python.exe");
        config["root"] = destination; config["studioApp"] = Path.Combine(destination, "QproFaceTracking.exe");
        Rewrite(config, Remap);
        foreach (var copy in copies.Where(c => Path.GetExtension(c.Target).Equals(".json", StringComparison.OrdinalIgnoreCase)))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(File.ReadAllText(copy.Target)); } catch (JsonException) { continue; }
            if (node is not null && Rewrite(node, text => { try { return Remap(text); } catch (IOException) { return text; } }))
                File.WriteAllText(copy.Target, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        if (originalConfig is not null)
        {
            Directory.CreateDirectory(Path.Combine(destination, "upgrade-backup"));
            File.WriteAllBytes(Path.Combine(destination, "upgrade-backup", "QproAutoStart-" + Guid.NewGuid().ToString("N")[..8] + ".json"), originalConfig);
        }
        foreach (var copy in copies) migratedHashes[copy.Target] = Hash(copy.Target);
    }

    static string[] PortableFiles(string root)
    {
        NoLinks(root);
        var files = new List<string>();
        foreach (var folder in new[] { "calibration", "config", "models", "captures", "training" })
            if (Directory.Exists(Path.Combine(root, folder))) files.AddRange(Files(Path.Combine(root, folder)));
        var output = Path.Combine(root, "output-settings.json");
        if (File.Exists(output)) { if ((File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked settings cannot be migrated."); files.Add(output); }
        return files.ToArray();
    }
    void Copy(string source, string target, string hash)
    {
        if (!ChildOf(target, destination)) throw new IOException("Unsafe migration destination.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        NoLinks(Path.GetDirectoryName(target)!);
        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked migration destination.");
        File.Copy(source, target, true);
        if (Hash(target) != hash || Hash(source) != hash) throw new IOException("A file changed during migration. Old installations were kept.");
        copies.Add((source, target, hash));
    }
    static bool References(JsonNode node, string file)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text.Equals(file, StringComparison.OrdinalIgnoreCase);
        return node is JsonObject obj ? obj.Any(p => p.Value is not null && References(p.Value, file)) : node is JsonArray array && array.Any(n => n is not null && References(n, file));
    }
    static bool ReferencesRoot(JsonNode node, string root)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return Path.IsPathFullyQualified(text) && (Full(text).Equals(root, StringComparison.OrdinalIgnoreCase) || ChildOf(text, root));
        return node is JsonObject obj ? obj.Any(p => p.Value is not null && ReferencesRoot(p.Value, root)) : node is JsonArray array && array.Any(n => n is not null && ReferencesRoot(n, root));
    }
    static bool Rewrite(JsonNode node, Func<string, string> map)
    {
        var changed = false;
        if (node is JsonObject obj)
            foreach (var (key, value) in obj.ToArray())
                if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text)) { var mapped = map(text); if (mapped != text) { obj[key] = mapped; changed = true; } }
                else if (value is not null) changed |= Rewrite(value, map);
        if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
                if (array[i] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) { var mapped = map(text); if (mapped != text) { array[i] = mapped; changed = true; } }
                else if (array[i] is {} child) changed |= Rewrite(child, map);
        return changed;
    }

    internal void EnsureStopped()
    {
        foreach (var process in Process.GetProcesses())
        using (process)
        {
            if (process.Id == Environment.ProcessId) continue;
            try
            {
                if (process.ProcessName.Equals("VRCFaceTracking", StringComparison.OrdinalIgnoreCase)) throw new IOException("Quit QFT+ and VRCFaceTracking, then try again. Finish or cancel calibration first.");
                var executable = process.MainModule?.FileName;
                if (executable is not null && directories.Keys.Any(root => ChildOf(executable, root)))
                {
                    var adb = Path.Combine(destination, "platform-tools", "adb.exe");
                    if (process.ProcessName.Equals("adb", StringComparison.OrdinalIgnoreCase) && File.Exists(adb))
                    {
                        using var stop = Process.Start(new ProcessStartInfo(adb, "kill-server") { UseShellExecute = false, CreateNoWindow = true });
                        if (stop is not null && stop.WaitForExit(5000) && stop.ExitCode == 0 && process.WaitForExit(5000)) continue;
                    }
                    throw new IOException("Close apps using the previous installation, then try again: " + process.ProcessName);
                }
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }
        foreach (var root in directories.Keys)
        {
            var status = Path.Combine(root, "autostart-status.json");
            if (!File.Exists(status)) continue;
            JsonObject state;
            try { state = ReadObject(status); } catch (JsonException) { continue; }
            if (state["state"]?.GetValue<string>() is not ("running" or "starting" or "waiting")) continue;
            if (state["pid"]?.GetValue<int>() is not {} pid) continue;
            try { using var process = Process.GetProcessById(pid); if (!process.HasExited) throw new IOException("Stop tracking and quit QFT+ before installing the update."); }
            catch (ArgumentException) { }
        }
    }

    internal void Commit()
    {
        var current = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        if (!(current ?? []).AsSpan().SequenceEqual(originalConfig ?? [])) throw new IOException("Settings changed during installation. Quit QFT+ and try again.");
        committedConfig = System.Text.Encoding.UTF8.GetBytes(config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllBytes(configPath + ".upgrade.tmp", committedConfig); File.Move(configPath + ".upgrade.tmp", configPath, true);
    }
    internal void Rollback()
    {
        if (committedConfig is null || !File.Exists(configPath) || !File.ReadAllBytes(configPath).AsSpan().SequenceEqual(committedConfig)) return;
        if (originalConfig is null) File.Delete(configPath);
        else { File.WriteAllBytes(configPath + ".upgrade.tmp", originalConfig); File.Move(configPath + ".upgrade.tmp", configPath, true); }
        committedConfig = null;
    }
    internal string[] CleanOldInstallations()
    {
        if (committedConfig is null) throw new InvalidOperationException("Settings must be committed before cleanup.");
        try { EnsureStopped(); } catch (IOException) { return oldRoots.ToArray(); }
        var kept = new List<string>();
        var current = ReadObject(configPath);
        if (current["root"]?.GetValue<string>() is not {} active || !Full(active).Equals(destination, StringComparison.OrdinalIgnoreCase)) return oldRoots.ToArray();
        foreach (var root in oldRoots)
        {
            try
            {
                if (ReferencesRoot(current, root)) throw new IOException("Settings still refer to this installation.");
                if (!ChildOf(root, apps) || Path.GetDirectoryName(root) != apps || root.Equals(destination, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe cleanup target.");
                var files = Files(root);
                if (!files.SequenceEqual(inventories[root], StringComparer.OrdinalIgnoreCase)) throw new IOException("Old installation changed.");
                foreach (var (file, hash) in originalHashes.Where(p => ChildOf(p.Key, root)))
                    if (Hash(file) != hash) throw new IOException("Old installation changed.");
                foreach (var copy in copies.Where(c => ChildOf(c.Source, root)))
                    if (Hash(copy.Source) != copy.Hash || !File.Exists(copy.Target) || Hash(copy.Target) != migratedHashes[copy.Target]) throw new IOException("Migrated data changed.");
                foreach (var file in files) { using var unlocked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); }
                Directory.Delete(root, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { kept.Add(root); }
        }
        return kept.ToArray();
    }
}
