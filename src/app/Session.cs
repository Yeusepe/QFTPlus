using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QFTPlus;
internal sealed class Session
{
    internal readonly string Root;
    internal readonly string ConfigPath;
    internal event Action<string>? Changed;
    internal Process? Hybrid;
    Tracking? tracking;
    bool attempted;
    SetupService? setup;
    CancellationTokenSource cancelStart = new();
    Task? hybridRestart;
    (Task<HeadsetCleanup?> Started, string Target)? thumbrest;
    DateTime hybridRetry;
    bool handsSession, hybridReady, hybridPending, stopping;
    const string HybridCleanupProblem = "Hybrid tracking could not confirm cleanup. Restart Virtual Desktop on the headset and SteamVR before starting tracking again. See the Hybrid log for details.";
    internal string HybridProblem = "";
    string? hybridIncompatible;
    internal string State = "Ready", Detail = "";
    internal int Progress;
    internal string? HelpTarget;
    internal string HelpCaption = "Help";
    internal string ErrorLog = "tracking.log";
    internal Session(string root, string? configPath = null) { Root = root; ConfigPath = configPath ?? SetupService.AutoPath; }
    internal static JsonObject Read(string path)
    {
        try { return CalibrationSettings.ReadJson(path); }
        catch (JsonException error)
        { throw new InvalidDataException($"{Path.GetFileName(path)} is damaged; QFT+ left it as it is. Delete it to go back to the defaults. ({error.Message})", error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidDataException($"QFT+ couldn’t read {Path.GetFileName(path)}. Try again in a moment. ({error.Message})", error); }
    }
    readonly Lock configLock = new();
    (DateTime, long) configStamp;
    JsonObject? configCache;
    internal JsonObject Config
    {
        get
        {
            var file = new FileInfo(ConfigPath);
            var stamp = file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
            lock (configLock)
            {
                if (configCache is null || stamp != configStamp) (configCache, configStamp) = (Read(ConfigPath), stamp);
                return (JsonObject)configCache.DeepClone();
            }
        }
    }
    internal bool Calibrated(string option)
    {
        try { return option != "pupilDilation" || Read(Path.Combine(Root,"calibration/qpro-pupil-dilation.json"))["format"]?.GetValue<string>() == "qpro-relative-pupil-v1"; }
        catch (InvalidDataException) { return false; }
    }
    internal bool FaceOn(string kind) => (Config["extraFaceOutput"]?.GetValue<bool>() ?? true) && Config[CalibrationSettings.FaceOutputKey(kind)]?.GetValue<bool>() != false;
    internal void DisableUncalibratedOutputs()
    {
        if(!Calibrated("pupilDilation") && Config["pupilDilation"]?.GetValue<bool>() == true) Save("pupilDilation",false);
    }
    internal string Use => Config["use"]?.GetValue<string>() is ("face" or "hands" or "both") and var use ? use : "both";
    internal bool Hands => Use != "face";
    internal bool HybridReady => hybridReady && Alive(Hybrid);
    internal bool IndependentGaze => Config["independentGaze"]?.GetValue<bool>() ?? true;
    internal double VergenceGain => Config["vergenceGain"] is JsonValue v && v.TryGetValue<double>(out var n) && double.IsFinite(n) ? Math.Clamp(n, 0, 3) : 1;
    internal string Python => PythonRuntime.Exe(Root);
    internal void CancelSetup() { setup?.CancelOperation(); cancelStart.Cancel(); }
    int notifiedProgress;
    internal void Notify(string state, string detail = "")
    {
        if (state == State && detail == Detail && Progress == notifiedProgress) return;
        State = state; Detail = detail; notifiedProgress = Progress; Changed?.Invoke(state);
    }
    internal void Save(string key, JsonNode? value)
    {
        var config = Config; config[key] = value;
        CalibrationSettings.WriteJson(ConfigPath, config);
        Saved();
    }
    void Saved() { lock (configLock) configCache = null; }
    internal void SetVergenceGain(double value)
    {
        if (!double.IsFinite(value) || value < 0 || value > 3) throw new ArgumentOutOfRangeException(nameof(value));
        Save("vergenceGain", JsonValue.Create(value));
        if (!Running || !IndependentGaze) return;
        using var socket = new UdpClient();
        var data = Encoding.ASCII.GetBytes(value.ToString(CultureInfo.InvariantCulture));
        try { socket.Send(data, data.Length, "127.0.0.1", 27277); }
        catch (SocketException error) { throw new IOException("Your change was saved. Stop and start tracking to apply it.", error); }
    }
    internal static bool Alive(Process? process) { try { return process is not null && !process.HasExited; } catch { return false; } }
    internal bool Running => tracking?.Running == true || handsSession;
    internal bool SettingUp => setup is not null;
    internal async Task Prepare()
    {
        if (Running) throw new IOException("Stop tracking before running setup.");
        setup = new SetupService(Root);
        setup.StageChanged += (title, detail) => { HelpTarget=setup.HelpTarget; HelpCaption=setup.HelpCaption; Progress=setup.StageProgress; Notify(title,detail); };
        try { await setup.SetupAsync(Config["connectionMode"]?.GetValue<string>()??"auto",
            Config["headsetSerial"]?.GetValue<string>()??"", Config["installModule"]?.GetValue<bool>()??true, Use); }
        finally { setup=null; }
        Save("studioApp",Environment.ProcessPath);
    }
    internal async Task<System.Collections.Generic.List<SetupService.Headset>> Discover()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await new SetupService(Root).DiscoverAsync(timeout.Token);
    }
    internal async Task Start(bool fromModule = false)
    {
        if (Running || (fromModule && attempted)) return;
        var use = Use;
        if (fromModule && use == "hands") return;
        attempted = true;
        var token = (cancelStart = new()).Token;
        await StopThumbrest();
        if (use == "hands" && SteamVr.IsSteamLink)
            throw new IOException("Hand tracking needs Virtual Desktop, and SteamVR is using Steam Link. Connect with Virtual Desktop, or choose face tracking in Settings.");
        if (!fromModule && (Config["setupOnStart"]?.GetValue<bool>()??true))
        {
            await Prepare();
        }
        else if (fromModule && (Config["setupOnStart"]?.GetValue<bool>()??true))
        {
            setup=new SetupService(Root);
            try
            {
                using var discovery=CancellationTokenSource.CreateLinkedTokenSource(token);discovery.CancelAfter(TimeSpan.FromSeconds(30));
                var found=await setup.ConnectQuestAsync(discovery.Token,Config["connectionMode"]?.GetValue<string>()??"auto",Config["headsetSerial"]?.GetValue<string>()??"");
                Save("adbTarget",found.Target);
            }
            finally {setup=null;}
        }
        DisableUncalibratedOutputs();
        SteamVr.ConfigureSteamLink();
        var config = Config;
        if (!string.Equals(config["root"]?.GetValue<string>(), Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Complete setup in this app before enabling auto-start.");
        HybridProblem = "";
        if (!PythonRuntime.Ready(Root)) { Notify("Preparing components", "One-time setup…"); await PythonRuntime.EnsureAsync(Root, token); }
        await Adb.EnsureAsync(Adb.Exe(Root), config["adbTarget"]?.GetValue<string>(), token);
        if (config["steamvrDriver"]?.GetValue<bool>() == true && config["adbTarget"]?.GetValue<string>() is { Length: >0 } target) thumbrest = (Thumbrest.StartAsync(this, target), target);
        if (use == "hands")
        {
            handsSession = true;
            Notify("Connecting", "Starting hand tracking…");
            if (config["openVrApps"]?.GetValue<bool>()??true) OpenVrApps(face: false);
            try { await StartHybrid(); }
            catch { handsSession = false; throw; }
            return;
        }
        tracking = new Tracking(this, config["adbTarget"]?.GetValue<string>() ?? "", IndependentGaze, VergenceGain);
        Notify("Connecting", "Starting the headset cameras…");
        if (!fromModule && (config["openVrApps"]?.GetValue<bool>()??true)) OpenVrApps(face: true);
        hybridPending = use == "both" && !SteamVr.IsSteamLink;
    }
    static void OpenVrApps(bool face)
    {
        if(!SteamVr.IsSteamLink&&!Processes.Running("VirtualDesktop.Streamer")&&File.Exists(SetupService.VirtualDesktopStreamer))Process.Start(new ProcessStartInfo(SetupService.VirtualDesktopStreamer){UseShellExecute=true})?.Dispose();
        OpenSteam("250820", "vrserver");
        if (face) OpenSteam("3329480", "VRCFaceTracking");
    }
    internal void Poll()
    {
        if (stopping) return;
        if (tracking is null && !handsSession) return;
        if (!Running)
        {
            ErrorLog = tracking!.ErrorLog;
            Notify("Tracking stopped", tracking.Error.Length > 0 ? tracking.Error : "Tracking ended unexpectedly.");
            return;
        }
        if (Hybrid is not null && !Alive(Hybrid) && Hybrid.ExitCode == 4)
            HybridProblem = hybridIncompatible ?? "This headset's controller runtime is not supported by hybrid tracking. See the Hybrid log for details.";
        if (Hybrid is not null && !Alive(Hybrid) && Hybrid.ExitCode is not (0 or 1 or 4))
            HybridProblem = HybridCleanupProblem;
        if (Hybrid is not null && !Alive(Hybrid) && Hybrid.ExitCode is (0 or 1) && hybridRestart is null && DateTime.UtcNow >= hybridRetry && Hands)
        {
            hybridRetry = DateTime.UtcNow.AddSeconds(15);
            hybridRestart = TryStartHybrid();
        }
        var via = (Config["adbTarget"]?.GetValue<string>()??"").Contains(':')?"Wi-Fi":"USB";
        if (handsSession)
        {
            if (HybridProblem.Length > 0) Notify("Hybrid tracking unavailable", HybridProblem);
            else if (HybridReady) Notify("Connected", "Quest Pro · "+via);
            else Notify("Waiting for headset", "Put on the headset, connect to this PC in Virtual Desktop, and enter SteamVR.");
            return;
        }
        switch (tracking!.State)
        {
            case "running":
                Notify("Connected", "Quest Pro · "+via);
                if (hybridPending && hybridRestart is null) { hybridPending = false; hybridRestart = TryStartHybrid(); }
                break;
            case "waiting": Notify("Waiting for headset", "Connect to this PC in Steam Link or Virtual Desktop and enter SteamVR."); break;
            case "starting": Notify("Connecting", "Starting the headset cameras…"); break;
        }
    }
    internal async Task Stop()
    {
        stopping = true;
        try
        {
            Notify("Stopping", "Restoring headset tracking…");
            if (hybridRestart is not null) await hybridRestart;
            var cleanupFailed = false;
            if (Hybrid is not null)
            {
                await StopChild(Hybrid);
                cleanupFailed = Hybrid.ExitCode is not (0 or 1 or 4);
                Hybrid.Dispose(); Hybrid = null;
            }
            handsSession = hybridPending = false;
            await StopThumbrest();
            if (tracking is not null) { await tracking.StopAsync(); tracking = null; }
            if (cleanupFailed) { HybridProblem = HybridCleanupProblem; Notify("Cleanup needs attention", HybridProblem); }
            else Notify("Ready");
        }
        finally { stopping = false; }
    }
    internal async Task StopThumbrest()
    {
        if (thumbrest is not (Task<HeadsetCleanup?> started, string target)) return;
        thumbrest = null;
        await Thumbrest.StopAsync(this, target, await started);
    }
    internal async Task StartHybrid()
    {
        if (stopping) return;
        if (SteamVr.IsSteamLink) return;
        if (Alive(Hybrid)) return;
        var target = Config["adbTarget"]?.GetValue<string>() ?? "";
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            if (await new SetupService(Root).HandSettingsProblemAsync(target, timeout.Token) is {} problem) throw new IOException(problem);
        if (stopping) return;
        hybridReady = false; HybridProblem = ""; hybridIncompatible = null;
        Hybrid?.Dispose(); Hybrid = Launch(Python, [Path.Combine(Root,"hybrid/hybrid.py"), "--target", target], "hybrid.log", line =>
        {
            if (line == "QROOT_READY") hybridReady = true;
            else if (line.StartsWith("QROOT_INCOMPATIBLE ")) hybridIncompatible = line["QROOT_INCOMPATIBLE ".Length..];
        });
    }
    async Task TryStartHybrid()
    {
        try { await StartHybrid(); }
        catch (Exception error) { HybridProblem = error.Message; Log(Path.Combine(Root,"hybrid.log"), "Couldn’t start hybrid hands: "+error.Message); }
        finally { hybridRestart = null; }
    }
    internal Process Launch(string exe, string[] args, string log, Action<string>? line = null, params (string Key, string Value)[] variables)
    {
        var info = Info(exe,args);
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        foreach (var (key, value) in variables) info.Environment[key] = value;
        Rotate(Path.Combine(Root,log), clear: true);
        var process = Process.Start(info) ?? throw new IOException("Could not start "+Path.GetFileName(exe));
        _ = Drain(process.StandardOutput); _ = Drain(process.StandardError);
        async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is {} text) { line?.Invoke(text); Log(Path.Combine(Root,log),text); } }
        return process;
    }
    internal static async Task StopChild(Process process)
    {
        try { process.StandardInput.Close(); } catch (Exception error) when (error is IOException or InvalidOperationException) { }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } }
    }
    static readonly Lock logLock = new();
    internal static void Log(string path, string text)
    {
        lock (logLock) try { File.AppendAllText(path, text + Environment.NewLine); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    internal static void Rotate(string path, bool clear = false)
    {
        lock (logLock)
            try { if (new FileInfo(path) is { Exists: true, Length: > 4 << 20 }) File.Move(path, path + ".old", true); if (clear) File.WriteAllText(path, ""); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
    internal ProcessStartInfo Info(string exe, string[] args)
    {
        var info = Processes.Info(exe,args); info.WorkingDirectory=Root;
        var config = Config;
        info.Environment["QPRO_ADB"]=Adb.Exe(Root); info.Environment["PYTHONUNBUFFERED"]="1";
        if (config["adbTarget"]?.GetValue<string>() is { Length: >0 } target) info.Environment["ANDROID_SERIAL"]=target;
        info.Environment["QFT_INFERENCE"]=config["inferenceDevice"]?.GetValue<string>()??"auto";
        info.Environment["QFT_GPU_INDEX"]=(config["gpuIndex"]?.GetValue<int>()??0).ToString();
        return info;
    }
    internal async Task<(int Code,string Output)> Run(string exe,string[] args,bool allowFailure=false,int seconds=600)
    {
        var (code,text)=await Processes.RunAsync(Info(exe,args),default,seconds);
        Rotate(Path.Combine(Root,"studio.log")); Log(Path.Combine(Root,"studio.log"),text);
        if(code!=0&&!allowFailure) throw new IOException("Couldn’t finish this step. The Components log in Settings has the details.");
        return (code,text);
    }
    internal async Task<string> Enroll(string prefix)
    {
        var anchors=await Run(Python,[Path.Combine(Root,"guided_session.py"),"check",prefix],allowFailure:true);
        if(anchors.Code!=0) throw new IOException(anchors.Output.Trim());
        return prefix+".anchors.npz";
    }
    internal const int ConsentVersion=1;
    internal bool Tester => Config["testerConsent"]?["version"]?.GetValue<int>() == ConsentVersion;
    internal string TesterId => Config["testerConsent"]?["tester"]?.GetValue<string>() ?? "";
    internal void JoinTesting()
    {
        var id=Guid.NewGuid().ToString("N").ToUpperInvariant();
        Save("testerConsent",new JsonObject{["version"]=ConsentVersion,["tester"]=$"QFT-{id[..4]}-{id[4..8]}",["time"]=DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")});
        Save("benchmarkMode",true);
    }
    internal void LeaveTesting(){Save("testerConsent",null);Save("benchmarkMode",false);}
    internal async Task<string> Package(string prefix)
    {
        var consent=Config["testerConsent"]??throw new InvalidOperationException("Become a tester first.");
        var args=new System.Collections.Generic.List<string>{Path.Combine(Root,"contribution.py"),prefix,"--tester",consent["tester"]!.GetValue<string>(),
            "--consent-version",consent["version"]!.GetValue<int>().ToString(),"--consent-time",consent["time"]!.GetValue<string>()};
        if(Config["faceEnrollment"]?.GetValue<string>() is {Length:>0} enrollment&&File.Exists(enrollment))args.AddRange(["--enrollment",enrollment]);
        var run=await Run(Python,args.ToArray(),allowFailure:true);
        var match=System.Text.RegularExpressions.Regex.Match(run.Output,@"^PACKAGE (.+) \d+\s*$",System.Text.RegularExpressions.RegexOptions.Multiline);
        if(run.Code!=0||!match.Success)throw new IOException("Couldn’t prepare the recording to share. "+run.Output.Trim().Split('\n').LastOrDefault());
        return match.Groups[1].Value.Trim();
    }
    internal void Apply(string kind,string result) { CalibrationSettings.SaveCalibration(Root,kind,result,ConfigPath); Saved(); }
    static void OpenSteam(string id,string process)
    {
        if(!Processes.Running(process)) Process.Start(new ProcessStartInfo("steam://rungameid/"+id){UseShellExecute=true})?.Dispose();
    }
}
