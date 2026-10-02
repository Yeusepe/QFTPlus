using System.Diagnostics;
using System.Text.Json.Nodes;

namespace QFTPlus;

internal static class SteamVrDriver
{
    const string Name = "qftplus";
    static readonly string Layout = Path.DirectorySeparatorChar + Path.Combine("steamvr", Name);

    internal static string? Register(string driver)
    {
        driver = Path.GetFullPath(driver);
        if (!File.Exists(Path.Combine(driver, "driver.vrdrivermanifest"))) throw new IOException("The driver is missing from this installation.");
        var tool = FindTool() ?? throw new IOException("SteamVR isn’t installed. Install it, start it once, then try again.");
        var unblocked = SteamVr.Unblock();
        var registered = Registered(tool);
        if (registered.Count == 1 && Same(registered[0], driver))
            return unblocked && SteamVr.Running() ? "Restart SteamVR to use the thumbrest and trigger." : null;
        foreach (var stale in registered.Where(path => !Same(path, driver))) Run(tool, "removedriver", stale);
        if (!registered.Any(path => Same(path, driver))) Run(tool, "adddriver", driver);
        var now = Registered(tool);
        if (now.Count != 1 || !Same(now[0], driver)) throw new IOException("SteamVR didn’t accept the driver.");
        return SteamVr.Running() ? "Restart SteamVR to use the thumbrest and trigger." : null;
    }

    internal static string? Unregister()
    {
        if (FindTool() is not { } tool) return null;
        var registered = Registered(tool);
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
        try
        {
            var runtimes = SteamVr.OpenVrPaths()?["runtime"]?.AsArray() ?? [];
            return runtimes.OfType<JsonValue>().Where(runtime => runtime.GetValueKind() == System.Text.Json.JsonValueKind.String)
                .Select(runtime => Path.Combine(runtime.GetValue<string>(), "bin", "win64", "vrpathreg.exe")).FirstOrDefault(File.Exists);
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException)
        { throw new IOException("SteamVR’s openvrpaths.vrpath file can’t be read: " + error.Message, error); }
    }

    static string Run(string tool, params string[] arguments)
    {
        var (code, text) = Processes.RunAsync(tool, arguments).GetAwaiter().GetResult();
        return code == -1 && text == "Timed out" ? throw new IOException("SteamVR’s driver tool didn’t respond.") : text;
    }

    static bool Same(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), right.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
}
