using System.ComponentModel;
using System.Diagnostics;

namespace QFTPlus;

internal static class Adb
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string[] Folders = ["QproFaceTracking", "QproFaceTracking.App", "QFTPlus"];

    internal static string Exe(string root) => Environment.GetEnvironmentVariable("QPRO_ADB") is { Length: > 0 } adb ? adb : Path.Combine(root, "platform-tools", "adb.exe");

    internal static bool IsOurs(string? exe) =>
        exe is not null && Folders.Any(folder => exe.StartsWith(Path.Combine(Local, folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    internal static string Quote(string command) => "'" + command.Replace("'", "'\\''") + "'";

    internal static string[] Su(string target, string command) => ["-s", target, "shell", "su -c " + Quote(command)];

    internal static async Task EnsureAsync(string adb, string? target = null, CancellationToken token = default)
    {
        if ((await Processes.RunAsync(adb, ["start-server"], token, 10)).Code != 0)
        {
            Stop();
            if ((await Processes.RunAsync(adb, ["start-server"], token, 15)).Code != 0)
                throw new IOException("adb didn’t start. Unplug the headset and plug it back in, or restart the PC, then try again.");
        }
        if (target is { Length: > 0 } && target.Contains(':')) await Processes.RunAsync(adb, ["connect", target], token, 10);
    }

    internal static void Stop()
    {
        foreach (var process in Process.GetProcessesByName("adb"))
            using (process)
            {
                try
                {
                    if (!IsOurs(process.MainModule?.FileName)) continue;
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            }
    }
}
