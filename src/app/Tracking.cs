using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace QFTPlus;

internal sealed partial class Tracking
{
    const string Relay = "/data/local/tmp/questpro-camera-relay-v10", Streamer = "/data/local/tmp/libquestpro-camera-streamer-v10.so";
    const string CameraLog = "/data/local/tmp/questpro-live-v10.log";
    const string Leftovers = "questpro-* libquestpro-* qft-thumbrest.log qft-permission-check-* qpro-eye-module-*.zip";
    internal const string EyeTarget = "/odm/etc/eyetracking/runtime/models/Seacliff_V1_5/fbnet/int8/experimental/bolt/bolt.ptl";
    const int StreamPort = 27273, MaxFps = 24;
    const string Log = "tracking.log";

    readonly Session session;
    readonly string adb, target;
    volatile bool eyes;
    internal volatile bool EyeControlReady;
    readonly bool headsetModel;
    readonly double vergence;
    readonly CancellationTokenSource stop = new();
    Process? relay;
    internal string State = "waiting", Error = "", ErrorLog = Log;
    internal sealed record StreamRate(double Fps, double Busy);
    internal StreamRate? Stream;
    internal string SlowCause => Stream is not { Fps: < 15 } rate ? "" : rate.Busy > .8 ? "pc" : "connection";
    internal Task Run { get; }

    internal Tracking(Session session, string target, bool eyes, double vergence)
    {
        this.session = session; this.target = target; this.eyes = eyes; this.vergence = vergence;
        headsetModel = HeadsetModelExperiment.Enabled(session);
        adb = Adb.Exe(session.Root);
        Session.Rotate(Path.Combine(session.Root, Log), clear: true);
        Run = Task.Run(RunAsync);
    }

    internal bool Running => !Run.IsCompleted;
    internal void SetIndependentEyes(bool enabled)
    {
        eyes = enabled;
        if (EyeControlReady) Session.SendEyeControl(enabled ? "eyes:1" : "eyes:0");
    }

    string? Direct => Regex.Match(target, @"^(\d{1,3}(?:\.\d{1,3}){3}):\d+$") is { Success: true } match ? match.Groups[1].Value : null;

    bool childrenStopped = true;
    internal async Task<bool> StopAsync() { stop.Cancel(); await Run; return childrenStopped; }

    string Restore => (headsetModel ? $"LD_LIBRARY_PATH={HeadsetModelExperiment.Directory}:/vendor/lib64 {HeadsetModelExperiment.Worker} --stop; " : "") + $"{Relay} --stop; rm -f {Relay} {Streamer} {CameraLog}";

    async Task RunAsync()
    {
        Process? gaze = null, receiver = null;
        HeadsetCleanup? cleanup = null;
        try
        {
            Write("Waiting for the rooted Quest, SteamVR and VRCFaceTracking");
            while (!await Ready()) await Task.Delay(2000, stop.Token);
            State = "starting";
            cleanup = await HeadsetCleanup.StartAsync(session, target, Restore, "headset-cleanup.log");
            await Root($"cd /data/local/tmp && rm -f {Leftovers}", allowFailure: true);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await StartCamera(token);
            StartMetaGazeAveraging();
            receiver = session.Launch(session.Python, [Path.Combine(session.Root, "receiver.py")], "face.log",
                line =>
                {
                    if (line == "CONNECTED") State = "running";
                    else if (line.Split(' ') is ["STREAM_RATE", var fps, var busy])
                        Stream = new(double.Parse(fps, System.Globalization.CultureInfo.InvariantCulture), double.Parse(busy, System.Globalization.CultureInfo.InvariantCulture));
                }, ("QFT_STREAM_TOKEN", token), ("QFT_STREAM_HOST", Direct ?? "127.0.0.1"),
                ("QFT_HEADSET_MODEL", headsetModel ? "1" : "0"));
            var eyeModel = SetupService.EyeModel(session.Root);
            var eyeCalibration = Path.Combine(session.Root, "calibration/qpro-independent-visual-axis-v2.json");
            if (eyes && !File.Exists(eyeModel)) throw new IOException(Loc.Get("TheIndependentEyeModelIsMissingRunSetupAgainTo"));
            if (eyes && !File.Exists(eyeCalibration)) throw new IOException(Loc.Get("IndependentEyeCalibrationIsMissingToUseStandardEyeTracking"));
            if (File.Exists(eyeModel) && File.Exists(eyeCalibration))
            {
                gaze = session.Launch(session.Python, [Path.Combine(session.Root, "independent_visual_axis_runtime.py"),
                    "--calibration", eyeCalibration, "--model", eyeModel, "--enabled", eyes ? "1" : "0",
                    "--vergence-gain", vergence.ToString(System.Globalization.CultureInfo.InvariantCulture)], "eyes.log", line =>
                    {
                        if (line != "QFT_EYES_CONTROL_READY") return;
                        EyeControlReady = true;
                        SetIndependentEyes(eyes);
                        Session.SendEyeControl(session.VergenceGain.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    });
                await Task.WhenAny(gaze.WaitForExitAsync(stop.Token), Task.Delay(7000, stop.Token));
                Check(gaze, "eyes.log");
            }
            var children = new[] { gaze, relay, receiver }.OfType<Process>().ToArray();
            await Task.WhenAny(children.Select(child => child.WaitForExitAsync(stop.Token)));
            foreach (var (child, log) in new[] { (gaze, "eyes.log"), (receiver, "face.log"), (relay, "questpro-live-relay.txt") })
                if (child is not null) Check(child, log);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Write("QFT_ERROR: " + error);
            if (Error.Length == 0) Error = error.Message;
        }
        finally
        {
            State = "stopping";
            EyeControlReady = false;
            averagingReady = false;
            childrenStopped = (await Task.WhenAll(new[] { receiver, gaze, averaging }.OfType<Process>().Select(Session.StopChild))).All(stopped => stopped);
            if (averaging is not null && averaging.HasExited && averaging.ExitCode != 0)
            {
                childrenStopped = false;
                AveragingError = Loc.Get("MetaGazeAveragingRestoreFailed");
                Write(AveragingError);
            }
            receiver?.Dispose(); gaze?.Dispose(); averaging?.Dispose();
            if (cleanup is not null)
            {
                if (!await cleanup.StopAsync()) Write("The headset didn't confirm its cleanup. It finishes on the headset once the connection closes.");
                Kill(relay);
                await Processes.RunAsync(adb, ["-s", target, "forward", "--remove", $"tcp:{StreamPort}"]);
            }
            Write(childrenStopped ? "Tracking stopped" : "Tracking cleanup finished, but a PC tracking process is still running.");
        }
    }

    async Task<bool> Ready()
    {
        if (!Processes.Running("vrserver") || !Processes.Running("VRCFaceTracking")) return false;
        if (target.Contains(':') && (await Processes.RunAsync(adb, ["-s", target, "get-state"], stop.Token, 5)).Text.Trim() != "device")
            await Processes.RunAsync(adb, ["connect", target], stop.Token, 5);
        return (await Processes.RunAsync(adb, ["-s", target, "shell", "su -c id"], stop.Token, 5, "uid=0(root)")).Text.Contains("uid=0(root)");
    }

    async Task StartCamera(string token)
    {
        Write("Starting the headset cameras");
        foreach (var (local, remote) in new[] { ("libquestpro-camera-streamer-v10.so", Streamer), ("questpro-camera-relay-v10", Relay) })
        {
            var file = Path.Combine(session.Root, local);
            if (!File.Exists(file)) throw new IOException(Loc.Format("APackagedHeadsetComponentIsMissingReinstallQFT", local));
            if ((await Processes.RunAsync(adb, ["-s", target, "push", file, remote], stop.Token, 60)).Code != 0) throw new IOException(Loc.Get("CopyingTheCameraComponentsToTheHeadsetFailedCheckThe"));
        }
        await Root($"chmod 755 {Relay}; chmod 644 {Streamer}; rm -f {CameraLog}; touch {CameraLog}; chmod 666 {CameraLog}");
        await Processes.RunAsync(adb, ["-s", target, "forward", "--remove", $"tcp:{StreamPort}"]);
        var inject = await session.Run(session.Python, [Path.Combine(session.Root, "camera_injector.py")], allowFailure: true, seconds: 90);
        Write(inject.Output);
        if (inject.Code != 0) throw new IOException(Loc.Get("StartingTheHeadsetCameraStreamFailedTheTrackingLogShows"));
        var bind = Direct is { } address ? $" --bind {address}" : "";
        var command = headsetModel ? $"LD_LIBRARY_PATH={HeadsetModelExperiment.Directory}:/vendor/lib64 {HeadsetModelExperiment.Worker} --model {HeadsetModelExperiment.Directory}" : Relay;
        relay = session.Launch(adb, Adb.Su(target, $"{command} --max-fps {MaxFps} --token {token}{bind}"), "questpro-live-relay.txt");
        await Task.Delay(800, stop.Token);
        if (relay.HasExited)
        {
            var log = Read("questpro-live-relay.txt");
            throw new IOException(Regex.Match(log, @"(?m)^SHARED_MEMORY_FAILED\b.*$") is { Success: true } failure
                ? Loc.Format("TheHeadsetCameraRelayCouldNotPrepareSharedMemorySend", failure.Value) : Loc.Get("TheHeadsetCameraRelayExitedDuringStartupSendQuestproLive"));
        }
        await Task.Delay(800, stop.Token);
        Write((await Root($"cat {CameraLog}", allowFailure: true)).Text);
        if (Direct is null && (await Processes.RunAsync(adb, ["-s", target, "forward", $"tcp:{StreamPort}", $"tcp:{StreamPort}"])).Code != 0) throw new IOException(Loc.Get("ADBPortForwardingFailedReconnectTheHeadsetThenTryAgain"));
    }

    async Task<(int Code, string Text)> Root(string command, bool allowFailure = false)
    {
        var result = await Processes.RunAsync(adb, Adb.Su(target, command), default, 30);
        if (result.Code != 0 && !allowFailure)
            throw new IOException(Loc.Format("TheHeadsetDidnTAcceptACommandMakeSureIt", command, result.Text.Trim()));
        return result;
    }

    void Check(Process child, string log)
    {
        if (!child.HasExited || stop.IsCancellationRequested) return;
        var lines = Read(log).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var reason = lines.LastOrDefault(l => l.StartsWith("QFT_ERROR:"))?["QFT_ERROR:".Length..].Trim()
            ?? (child == relay ? Loc.Get("TheConnectionToTheHeadsetEndedMakeSureItS") : lines.LastOrDefault()) ?? "";
        if (reason.Contains("EYE_SWAP_", StringComparison.Ordinal))
            reason = Loc.Get(reason.Contains("EYE_SWAP_UNSUPPORTED", StringComparison.Ordinal) ? "EyeSwapUnsupported"
                : reason.Contains("EYE_SWAP_BUSY", StringComparison.Ordinal) ? "EyeSwapBusy"
                : reason.Contains("EYE_SWAP_RESTORE_FAILED", StringComparison.Ordinal) ? "EyeSwapRestoreFailed" : "EyeSwapFailed");
        ErrorLog = log;
        throw new IOException(Regex.Replace(reason, @"^\w+(Error|Exception): ", "") is { Length: > 0 } text ? text : Loc.Format("StoppedUnexpectedly", log));
    }

    static void Kill(Process? process)
    {
        if (process is null) return;
        try { process.Kill(); process.WaitForExit(2000); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }

    string Read(string log)
    {
        try { using var reader = new StreamReader(new FileStream(Path.Combine(session.Root, log), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)); return reader.ReadToEnd(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }

    void Write(string text) => Session.Log(Path.Combine(session.Root, Log), $"{DateTime.Now:HH:mm:ss} {text.Trim()}");
}
