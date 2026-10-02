using System.ComponentModel;
using System.Diagnostics;

namespace QFTPlus;

internal sealed class HeadsetCleanup
{
    readonly Process shell;
    readonly TaskCompletionSource armed = new(TaskCreationOptions.RunContinuationsAsynchronously), done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    HeadsetCleanup(Session session, string target, string restore, string log)
    {
        var script = $"trap '' HUP PIPE; echo QFT_CLEANUP_ARMED; cat >/dev/null; {{ {restore}; }} >/dev/null 2>&1; echo QFT_CLEANUP_DONE";
        shell = session.Launch(Adb.Exe(session.Root), Adb.Su(target, script), log, line =>
        {
            if (line == "QFT_CLEANUP_ARMED") armed.TrySetResult();
            else if (line == "QFT_CLEANUP_DONE") done.TrySetResult();
        });
    }

    internal static async Task<HeadsetCleanup> StartAsync(Session session, string target, string restore, string log)
    {
        var cleanup = new HeadsetCleanup(session, target, restore, log);
        if (await Task.WhenAny(cleanup.armed.Task, cleanup.shell.WaitForExitAsync(), Task.Delay(15000)) == cleanup.armed.Task) return cleanup;
        cleanup.Kill();
        throw new IOException("The headset didn't start its cleanup shell. Make sure it's awake and Magisk allows Shell, then try again.");
    }

    internal async Task<bool> StopAsync()
    {
        try { shell.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        var exited = shell.WaitForExitAsync();
        await Task.WhenAny(done.Task, exited, Task.Delay(30000));
        if (exited.IsCompleted) await Task.WhenAny(done.Task, Task.Delay(1000));
        Kill();
        return done.Task.IsCompleted;
    }

    void Kill()
    {
        try { shell.Kill(); } catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        shell.Dispose();
    }
}
