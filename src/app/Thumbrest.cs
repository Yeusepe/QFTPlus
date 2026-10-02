namespace QFTPlus;
internal static class Thumbrest
{
    const string Remote = "/data/local/tmp/qft-thumbrest", Port = "tcp:27055";

    internal static async Task<HeadsetCleanup?> StartAsync(Session session, string target)
    {
        HeadsetCleanup? cleanup = null;
        try
        {
            var adb = Adb.Exe(session.Root);
            cleanup = await HeadsetCleanup.StartAsync(session, target, $"{Remote} --stop; rm -f {Remote}", "thumbrest.log");
            await session.Run(adb, Adb.Su(target, Remote + " --stop"), allowFailure: true, seconds: 15);
            await session.Run(adb, ["-s", target, "push", Path.Combine(session.Root, "qft-thumbrest"), Remote], seconds: 60);
            await session.Run(adb, ["-s", target, "shell", "chmod 755 " + Remote], seconds: 15);
            await session.Run(adb, Adb.Su(target, Remote + " --daemon"), seconds: 15);
            await session.Run(adb, ["-s", target, "forward", Port, Port], seconds: 15);
            return cleanup;
        }
        catch (Exception error)
        {
            Log(session, "Thumbrest trackpad unavailable: " + error.Message);
            if (cleanup is not null) await cleanup.StopAsync();
            return null;
        }
    }

    internal static async Task StopAsync(Session session, string target, HeadsetCleanup? cleanup)
    {
        if (cleanup is not null && !await cleanup.StopAsync()) Log(session, "The headset didn't confirm the thumbrest trackpad stopped. It stops once the connection closes.");
        try { await session.Run(Adb.Exe(session.Root), ["-s", target, "forward", "--remove", Port], allowFailure: true, seconds: 15); }
        catch (Exception error) { Log(session, "Stopping the thumbrest trackpad: " + error.Message); }
    }

    static void Log(Session session, string line) => Session.Log(Path.Combine(session.Root, "studio.log"), line);
}
