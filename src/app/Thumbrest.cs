using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace QFTPlus;
internal static class Thumbrest
{
    const string Remote = "/data/local/tmp/qft-thumbrest", Port = "tcp:27055";

    internal static async Task StartAsync(Session session, string target)
    {
        try
        {
            var adb = Path.Combine(session.Root, "platform-tools/adb.exe");
            var local = Path.Combine(session.Root, "qft-thumbrest");
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(local))).ToLowerInvariant();
            var installed = await session.Run(adb, ["-s", target, "shell", "sha256sum " + Remote], allowFailure: true, seconds: 15);
            if (!installed.Output.TrimStart().StartsWith(hash, StringComparison.Ordinal))
            {
                await session.Run(adb, ["-s", target, "shell", $"su -c '{Remote} --stop'"], allowFailure: true, seconds: 15);
                await session.Run(adb, ["-s", target, "push", local, Remote], seconds: 60);
                await session.Run(adb, ["-s", target, "shell", "chmod 755 " + Remote], seconds: 15);
            }
            await session.Run(adb, ["-s", target, "shell", $"su -c '{Remote} --daemon'"], seconds: 15);
            await session.Run(adb, ["-s", target, "forward", Port, Port], seconds: 15);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log(session, "Thumbrest trackpad unavailable: " + error.Message);
        }
    }

    internal static async Task StopAsync(Session session, string target)
    {
        var adb = Path.Combine(session.Root, "platform-tools/adb.exe");
        try
        {
            await session.Run(adb, ["-s", target, "shell", $"su -c '{Remote} --stop'"], allowFailure: true, seconds: 15);
            await session.Run(adb, ["-s", target, "forward", "--remove", Port], allowFailure: true, seconds: 15);
        }
        catch (IOException error) { Log(session, "Stopping the thumbrest trackpad: " + error.Message); }
    }

    static void Log(Session session, string line)
    {
        try { File.AppendAllText(Path.Combine(session.Root, "studio.log"), line + Environment.NewLine); } catch (IOException) { }
    }
}
