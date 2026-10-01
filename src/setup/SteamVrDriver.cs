using System.Diagnostics;
using System.Text.Json.Nodes;

namespace QproFaceTracking.Hub;

internal static class SteamVrDriver
{
    const string Name = "qftplus";
    static readonly string Layout = Path.DirectorySeparatorChar + Path.Combine("steamvr", Name);

    internal static string? Register(string driver, Action<string>? log = null)
    {
        driver = Path.GetFullPath(driver);
        if (!File.Exists(Path.Combine(driver, "driver.vrdrivermanifest"))) throw new IOException("The driver is missing from this installation.");
        var tool = FindTool() ?? throw new IOException("SteamVR isn’t installed. Install it, start it once, then try again.");
        _log = log;
        log?.Invoke("SteamVR driver tool: " + tool);
        var unblocked = SteamVr.Unblock();
        if (unblocked) log?.Invoke("Cleared SteamVR’s safe-mode block on the driver.");
        var registered = Registered(tool);
        log?.Invoke(registered.Count == 0 ? "No QFT+ driver registered yet." : "Registered now: " + string.Join(" | ", registered));
        if (registered.Count == 1 && Same(registered[0], driver))
        {
            log?.Invoke("Already registered here; nothing to change.");
            return unblocked && SteamVr.Running() ? "Restart SteamVR to use the thumbrest and trigger." : null;
        }
        foreach (var stale in registered.Where(path => !Same(path, driver))) Run(tool, "removedriver", stale);
        if (!registered.Any(path => Same(path, driver))) Run(tool, "adddriver", driver);
        var now = Registered(tool);
        log?.Invoke("Registered after: " + string.Join(" | ", now));
        if (now.Count != 1 || !Same(now[0], driver)) throw new IOException("SteamVR didn’t accept the driver.");
        return SteamVr.Running() ? "Restart SteamVR to use the thumbrest and trigger." : null;
    }

    internal static string? Unregister(Action<string>? log = null)
    {
        _log = log;
        if (FindTool() is not { } tool) { log?.Invoke("SteamVR isn’t installed; nothing to remove."); return null; }
        var registered = Registered(tool);
        log?.Invoke(registered.Count == 0 ? "No QFT+ driver registered." : "Removing: " + string.Join(" | ", registered));
        foreach (var path in registered) Run(tool, "removedriver", path);
        if (Registered(tool).Count != 0) throw new IOException("SteamVR kept the driver registered.");
        return registered.Count > 0 && SteamVr.Running() ? "Restart SteamVR to finish turning off the driver." : null;
    }

    internal static bool Loaded()
    {
        var processes = Process.GetProcessesByName("vrserver");
        try
        {
            return processes.Any(process =>
            {
                try { return process.Modules.Cast<ProcessModule>().Any(module => module.ModuleName.Equals("driver_qftplus.dll", StringComparison.OrdinalIgnoreCase)); }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return !process.HasExited; }
            });
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    static List<string> Registered(string tool)
    {
        var paths = new List<string>();
        foreach (var line in Run(tool, "show").Split('\n'))
        {
            var separator = line.IndexOf(" : ", StringComparison.Ordinal);
            if (separator < 0 || !line.StartsWith('\t')) continue;
            var (name, path) = (line[..separator].Trim(), line[(separator + 3)..].Trim());
            if (name == Name || path.EndsWith(Layout, StringComparison.OrdinalIgnoreCase)) paths.Add(path);
        }
        return paths;
    }

    static string? FindTool()
    {
        var registry = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
        if (!File.Exists(registry)) return null;
        try
        {
            var runtimes = JsonNode.Parse(File.ReadAllText(registry))?["runtime"]?.AsArray() ?? [];
            return runtimes.OfType<JsonValue>().Where(runtime => runtime.GetValueKind() == System.Text.Json.JsonValueKind.String)
                .Select(runtime => Path.Combine(runtime.GetValue<string>(), "bin", "win64", "vrpathreg.exe")).FirstOrDefault(File.Exists);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        { throw new IOException("SteamVR’s openvrpaths.vrpath file can’t be read: " + error.Message, error); }
    }

    [ThreadStatic] static Action<string>? _log;

    static string Run(string tool, params string[] arguments)
    {
        _log?.Invoke("vrpathreg " + string.Join(' ', arguments));
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("SteamVR’s driver tool didn’t start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(); throw new IOException("SteamVR’s driver tool didn’t respond."); }
        var text = output.Result + errors.Result;
        if (arguments is not ["show"]) _log?.Invoke($"  exit {process.ExitCode}: " + (text.Trim().Length > 0 ? text.Trim().Replace(Environment.NewLine, " | ") : "(no output)"));
        return text;
    }

    static bool Same(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), right.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
