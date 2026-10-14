using System.ComponentModel;
using System.Diagnostics;
using NAudio.Wave;

namespace QFTPlus;

internal sealed class HeadsetMicrophone(Session session) : IWaveIn
{
    const string Remote = "/data/local/tmp/qft-mic";
    readonly CancellationTokenSource stop = new();
    Task? run;

    public WaveFormat WaveFormat { get; set; } = new(16000, 16, 1);
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    internal bool Connected { get; private set; }

    public void StartRecording() => run ??= Task.Run(RunAsync);
    public void StopRecording() => stop.Cancel();
    public void Dispose() => stop.Cancel();

    async Task RunAsync()
    {
        Exception? failure = null;
        HeadsetCleanup? cleanup = null;
        try
        {
            var adb = Adb.Exe(session.Root);
            var target = session.Config["adbTarget"]?.GetValue<string>() ?? "";
            var local = Path.Combine(session.Root, "qft-mic");
            if (!File.Exists(local)) throw new IOException(Loc.Format("APackagedHeadsetComponentIsMissingReinstallQFT", "qft-mic"));
            await Adb.EnsureAsync(adb, target, stop.Token);
            while ((await Processes.RunAsync(adb, ["-s", target, "shell", "su -c id"], stop.Token, 5, "uid=0(root)")).Text.Contains("uid=0(root)") is false)
            {
                await Task.Delay(2000, stop.Token);
                if (target.Contains(':')) await Processes.RunAsync(adb, ["connect", target], stop.Token, 5);
            }
            Connected = true;
            cleanup = await HeadsetCleanup.StartAsync(session, target, $"pkill -x qft-mic; rm -f {Remote} {Remote}.new", "lipsync-headset.log");
            if ((await Processes.RunAsync(adb, ["-s", target, "push", local, Remote + ".new"], stop.Token, 60)).Code != 0 ||
                (await Processes.RunAsync(adb, ["-s", target, "shell", $"chmod 755 {Remote}.new && mv -f {Remote}.new {Remote}"], stop.Token, 15)).Code != 0)
                throw new IOException(Loc.Get("HeadsetMicrophoneCopyFailed"));
            await Stream(adb, target);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException) { failure = error; }
        finally
        {
            if (cleanup is not null) await cleanup.StopAsync();
            RecordingStopped?.Invoke(this, new StoppedEventArgs(failure));
        }
    }

    async Task Stream(string adb, string target)
    {
        var info = Processes.Info(adb, Adb.Su(target, $"exec {Remote} --rate {WaveFormat.SampleRate}"));
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new IOException(Loc.Format("CouldNotStart", "adb"));
        var errors = process.StandardError.ReadToEndAsync();
        using var kill = stop.Token.Register(() => { try { process.Kill(); } catch (Exception error) when (error is InvalidOperationException or Win32Exception) { } });
        var pcm = process.StandardOutput.BaseStream;
        var buffer = new byte[WaveFormat.AverageBytesPerSecond / 50];
        int held = 0, read;
        while ((read = await pcm.ReadAsync(buffer.AsMemory(held), stop.Token)) > 0)
        {
            held += read;
            var whole = held - held % WaveFormat.BlockAlign;
            DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, whole));
            buffer.AsSpan(whole, held - whole).CopyTo(buffer);
            held -= whole;
        }
        stop.Token.ThrowIfCancellationRequested();
        var reason = (await errors).Split('\n').LastOrDefault(l => l.StartsWith("QFT_ERROR: "))?["QFT_ERROR: ".Length..].Trim();
        throw new IOException(reason is null ? Loc.Get("HeadsetMicrophoneDisconnected") : Loc.Format("HeadsetMicrophoneStopped", reason));
    }
}
