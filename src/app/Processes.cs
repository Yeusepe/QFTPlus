using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace QFTPlus;

internal static class Processes
{
    internal static bool Running(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    internal static ProcessStartInfo Info(string exe, IEnumerable<string> args) => new(exe, args) { CreateNoWindow = true };

    internal static Task<(int Code, string Text)> RunAsync(string exe, IEnumerable<string> args, CancellationToken token = default, int seconds = 15, string? successPrefix = null) =>
        RunAsync(Info(exe, args), token, seconds, successPrefix);

    internal static async Task<(int Code, string Text)> RunAsync(ProcessStartInfo info, CancellationToken token = default, int seconds = 15, string? successPrefix = null, Stream? output = null)
    {
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        Process? started;
        try { started = Process.Start(info); }
        catch (Win32Exception failure) { throw new IOException(Path.GetFileName(info.FileName) + " couldn’t start: " + failure.Message, failure); }
        using var process = started ?? throw new IOException(Path.GetFileName(info.FileName) + " didn’t start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var text = new StringBuilder();
            if (output is not null) await process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token);
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (successPrefix is not null && line.StartsWith(successPrefix, StringComparison.Ordinal)) return (0, line);
                text.AppendLine(line);
            }
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, text + await error);
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return (-1, "Timed out");
        }
        finally { try { process.Kill(); } catch (Exception failure) when (failure is Win32Exception or InvalidOperationException) { } }
    }
}
