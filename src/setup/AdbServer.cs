using System.ComponentModel;
using System.Diagnostics;

namespace QproFaceTracking.Hub;

internal static class AdbServer
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static readonly string[] Folders = ["QproFaceTracking", "QproFaceTracking.App", "QFTPlus"];

    internal static bool IsOurs(string? exe) =>
        exe is not null && Folders.Any(folder => exe.StartsWith(Path.Combine(Local, folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    internal static async Task EnsureAsync(string adb, string? target = null, CancellationToken token = default)
    {
        if (await RunAsync(adb, ["start-server"], 10, token) != 0)
        {
            Stop();
            if (await RunAsync(adb, ["start-server"], 15, token) != 0)
                throw new IOException("adb didn’t start. Unplug the headset and plug it back in, or restart the PC, then try again.");
        }
        if (target is { Length: > 0 } && target.Contains(':')) await RunAsync(adb, ["connect", target], 10, token);
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

    internal static async Task<int> RunAsync(string adb, string[] arguments, int seconds, CancellationToken token = default)
    {
        var info = new ProcessStartInfo(adb) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException("adb didn’t start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var output = Task.WhenAll(process.StandardOutput.ReadToEndAsync(timeout.Token), process.StandardError.ReadToEndAsync(timeout.Token));
        try { await process.WaitForExitAsync(timeout.Token); await output; return process.ExitCode; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { process.Kill(); } catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            return -1;
        }
    }
}
