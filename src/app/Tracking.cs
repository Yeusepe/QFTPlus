using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace QFTPlus;

internal sealed class Tracking
{
    const string Relay = "/data/local/tmp/questpro-camera-relay-v9", Streamer = "/data/local/tmp/libquestpro-camera-streamer-v9.so";
    const string CameraLog = "/data/local/tmp/questpro-live-v9.log";
    const string Legacy = "questpro-camera-relay-v8 libquestpro-camera-streamer-v8.so questpro-live-v8.log questpro-live-v8-shared.bin "
        + "questpro-relay-v8.log questpro-relay-v8.pid questpro-live-v3.log questpro-camera-injector libquestpro-camera-streamer.so "
        + "model_patcher.log model_patcher.zip bolt_patched.ptl qft-thumbrest.log qpro-eye-module-*.zip qft-permission-check-*";
    const string EyeTarget = "/odm/etc/eyetracking/runtime/models/Seacliff_V1_5/fbnet/int8/experimental/bolt/bolt.ptl";
    const string EyeCopy = "/data/local/tmp/qpro-seacliff-independent-axes.ptl";
    const string EyeProperty = "persist.device_config.oculus_shared_vision.oculus_eyetracking_enable_experimental_model";
    const int StreamPort = 27273, MaxFps = 24;
    const string Log = "tracking.log";

    readonly Session session;
    readonly string adb, target;
    readonly bool eyes;
    readonly double vergence;
    readonly CancellationTokenSource stop = new();
    Process? relay;
    bool mounted;
    internal string State = "waiting", Error = "", ErrorLog = Log;
    internal Task Run { get; }

    internal Tracking(Session session, string target, bool eyes, double vergence)
    {
        this.session = session; this.target = target; this.eyes = eyes; this.vergence = vergence;
        adb = Adb.Exe(session.Root);
        File.WriteAllText(Path.Combine(session.Root, Log), "");
        Run = Task.Run(RunAsync);
    }

    internal bool Running => !Run.IsCompleted;

    string? Direct => Regex.Match(target, @"^(\d{1,3}(?:\.\d{1,3}){3}):\d+$") is { Success: true } match ? match.Groups[1].Value : null;

    internal async Task StopAsync() { stop.Cancel(); await Run; }

    async Task RunAsync()
    {
        Process? gaze = null, receiver = null;
        var prepared = false;
        try
        {
            Write("Waiting for the rooted Quest, SteamVR and VRCFaceTracking");
            while (!await Ready()) await Task.Delay(2000, stop.Token);
            State = "starting";
            prepared = true;
            await Root($"{Relay} --stop; cd /data/local/tmp && rm -f {Legacy}", allowFailure: true);
            if (eyes)
            {
                await MountEyeModel();
                gaze = session.Launch(session.Python, [Path.Combine(session.Root, "independent_visual_axis_runtime.py"),
                    "--calibration", Path.Combine(session.Root, "calibration/qpro-independent-visual-axis-v2.json"),
                    "--vergence-gain", vergence.ToString(System.Globalization.CultureInfo.InvariantCulture)], "eyes.log");
                await Task.WhenAny(gaze.WaitForExitAsync(stop.Token), Task.Delay(7000, stop.Token));
                Check(gaze, "eyes.log");
            }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            await StartCamera(token);
            receiver = session.Launch(session.Python, [Path.Combine(session.Root, "receiver.py")], "face.log",
                line => { if (line == "CONNECTED") State = "running"; }, ("QFT_STREAM_TOKEN", token), ("QFT_STREAM_HOST", Direct ?? "127.0.0.1"));
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
            await Task.WhenAll(new[] { receiver, gaze }.OfType<Process>().Select(Session.StopChild));
            if (prepared)
            {
                await Root($"{Relay} --stop; rm -f {Relay} {Streamer} {CameraLog}", allowFailure: true);
                Kill(relay);
                await Processes.RunAsync(adb, ["-s", target, "forward", "--remove", $"tcp:{StreamPort}"]);
            }
            if (mounted) await RestoreEyeModel();
            Write("Tracking stopped");
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
        foreach (var (local, remote) in new[] { ("libquestpro-camera-streamer-v9.so", Streamer), ("questpro-camera-relay-v9", Relay) })
        {
            var file = Path.Combine(session.Root, local);
            if (!File.Exists(file)) throw new IOException($"A packaged headset component is missing: {local}. Reinstall QFT+.");
            if ((await Processes.RunAsync(adb, ["-s", target, "push", file, remote], stop.Token, 60)).Code != 0) throw new IOException("Copying the camera components to the headset failed. Check the connection, then try again.");
        }
        await Root($"chmod 755 {Relay}; chmod 644 {Streamer}; rm -f {CameraLog}; touch {CameraLog}; chmod 666 {CameraLog}");
        await Processes.RunAsync(adb, ["-s", target, "forward", "--remove", $"tcp:{StreamPort}"]);
        var inject = await session.Run(session.Python, [Path.Combine(session.Root, "camera_injector.py")], allowFailure: true, seconds: 90);
        Write(inject.Output);
        if (inject.Code != 0) throw new IOException("Starting the headset camera stream failed. The Tracking log shows why.");
        var bind = Direct is { } address ? $" --bind {address}" : "";
        relay = session.Launch(adb, ["-s", target, "shell", "su -c " + Adb.Quote($"{Relay} --max-fps {MaxFps} --token {token}{bind}")], "questpro-live-relay.txt");
        await Task.Delay(800, stop.Token);
        if (relay.HasExited)
        {
            var log = Read("questpro-live-relay.txt");
            throw new IOException((Regex.Match(log, @"(?m)^SHARED_MEMORY_FAILED\b.*$") is { Success: true } failure
                ? "The headset camera relay could not prepare shared memory. " + failure.Value : "The headset camera relay exited during startup.") + " Send questpro-live-relay.txt.");
        }
        await Task.Delay(800, stop.Token);
        Write((await Root($"cat {CameraLog}", allowFailure: true)).Text);
        if (Direct is null && (await Processes.RunAsync(adb, ["-s", target, "forward", $"tcp:{StreamPort}", $"tcp:{StreamPort}"])).Code != 0) throw new IOException("ADB port forwarding failed. Reconnect the headset, then try again.");
    }

    async Task MountEyeModel()
    {
        Write("Mounting the independent eye model");
        var local = SetupService.EyeModel(session.Root);
        if (!File.Exists(local)) throw new IOException("The independent eye model is missing. Run setup again to rebuild it.");
        if (!File.Exists(Path.Combine(session.Root, "calibration/qpro-independent-visual-axis-v2.json")))
            throw new IOException("Independent eye calibration is missing. To use standard eye tracking, turn off Independent eye gaze in Settings.");
        if ((await Root($"grep -F '{EyeTarget}' /proc/mounts", allowFailure: true)).Text.Trim().Length > 0) await RestoreEyeModel();
        await Root($"umount '{EyeTarget}'", allowFailure: true);
        mounted = true;
        if ((await Processes.RunAsync(adb, ["-s", target, "push", local, EyeCopy], stop.Token, 60)).Code != 0) throw new IOException("Couldn't copy the eye model to the headset. Check the connection, then try again.");
        await Root($"chown root:root '{EyeCopy}' && chmod 0644 '{EyeCopy}' && chcon u:object_r:vendor_configs_file:s0 '{EyeCopy}' && mount --bind '{EyeCopy}' '{EyeTarget}'");
        using (var stream = File.OpenRead(local))
            if (!(await Root($"sha256sum '{EyeTarget}'")).Text.StartsWith(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), StringComparison.Ordinal))
                throw new IOException("The eye model on the headset didn't match the one on this PC. Try again.");
        await Root($"setprop {EyeProperty} true && stop trackingservice && start trackingservice");
        await WaitForTrackingService();
    }

    async Task RestoreEyeModel()
    {
        await Root($"stop trackingservice; setprop {EyeProperty} false; umount '{EyeTarget}'; start trackingservice", allowFailure: true);
        try { await WaitForTrackingService(); }
        catch (IOException error) { Write(error.Message); }
        await Root($"rm -f '{EyeCopy}'", allowFailure: true);
    }

    async Task WaitForTrackingService()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if ((await Root("getprop init.svc.trackingservice", allowFailure: true)).Text.Trim() == "running") { await Task.Delay(1500); return; }
            await Task.Delay(250);
        }
        throw new IOException("The headset's tracking service didn't restart. Restart the headset, then try again.");
    }

    async Task<(int Code, string Text)> Root(string command, bool allowFailure = false)
    {
        var result = await Processes.RunAsync(adb, ["-s", target, "shell", "su -c " + Adb.Quote(command)], default, 30);
        if (result.Code != 0 && !allowFailure)
            throw new IOException($"The headset didn't accept a command. Make sure it's awake and connected, then try again. ({command}: {result.Text.Trim()})");
        return result;
    }

    void Check(Process child, string log)
    {
        if (!child.HasExited || stop.IsCancellationRequested) return;
        var lines = Read(log).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var reason = lines.LastOrDefault(l => l.StartsWith("QFT_ERROR:"))?["QFT_ERROR:".Length..].Trim() ?? lines.LastOrDefault() ?? "";
        ErrorLog = log;
        throw new IOException(Regex.Replace(reason, @"^\w+(Error|Exception): ", "") is { Length: > 0 } text ? text : $"{log} stopped unexpectedly.");
    }

    static void Kill(Process? process)
    {
        if (process is null) return;
        try { process.Kill(true); process.WaitForExit(2000); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }

    string Read(string log) { try { return File.ReadAllText(Path.Combine(session.Root, log)); } catch (IOException) { return ""; } }

    void Write(string text)
    {
        try { File.AppendAllText(Path.Combine(session.Root, Log), $"{DateTime.Now:HH:mm:ss} {text.Trim()}{Environment.NewLine}"); } catch (IOException) { }
    }
}
