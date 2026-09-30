using System;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using QproFaceTracking.Hub;

namespace QFTPlus;
internal sealed class Session
{
    internal readonly string Root;
    internal readonly string ConfigPath;
    internal event Action<string>? Changed;
    internal Process? Runtime, Hybrid;
    string? stop;
    bool attempted;
    SetupService? setup;
    CancellationTokenSource? discoveryCancel;
    Task? hybridRestart;
    (Task Started, string Target)? thumbrest;
    DateTime hybridRetry;
    bool handsSession, hybridReady, hybridPending, stopping;
    const string HybridCleanupProblem = "Hybrid tracking could not confirm cleanup. Restart Virtual Desktop on the headset and SteamVR before starting tracking again. See the Hybrid log for details.";
    internal string HybridProblem = "";
    internal string State = "Ready", Detail = "";
    internal int Progress;
    internal string? HelpTarget;
    internal string HelpCaption = "Help";
    internal string ErrorLog = "autostart.log";
    internal Session(string root, string? configPath = null) { Root = root; ConfigPath = configPath ?? SetupService.AutoPath; }
    internal static JsonObject Read(string path) { try { return CalibrationSettings.ReadJson(path); } catch { return new(); } }
    internal JsonObject Config => File.Exists(ConfigPath) ? CalibrationSettings.ReadJson(ConfigPath) : new();
    internal bool Calibrated(string option) => option switch
    {
        "extraFaceOutput" => !Legacy || CalibrationSettings.FaceGroups.Any(group => FaceCalibrated(group.Kind)),
        "pupilDilation" => Read(Path.Combine(Root,"calibration/qpro-pupil-dilation.json"))["format"]?.GetValue<string>() == "qpro-relative-pupil-v1",
        _ => true
    };
    internal static readonly string[] UniversalGroups = ["puff", "brows"];
    internal bool Legacy => Config["faceEngine"]?.GetValue<string>() == "legacy" && LegacyInstalled;
    internal bool LegacyInstalled => File.Exists(Path.Combine(Root, "runtime", "legacy-ready.json"));
    internal async Task InstallLegacy(Action<string>? line = null)
    {
        var (code, output) = await Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Root, "setup-runtime.ps1"), "-Legacy"], allowFailure: true, line: line);
        if (code != 0 || !LegacyInstalled)
            throw new IOException(output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.EndsWith("try again.") || l.EndsWith("setup.log.")) ?? "The older models couldn’t be set up. The Components log in Settings has the details.");
    }
    internal bool FaceCalibrated(string kind) => Legacy ? File.Exists(Config[CalibrationSettings.FaceModelKey(kind)]?.GetValue<string>()) : UniversalGroups.Contains(kind);
    internal bool FaceOn(string kind) => FaceCalibrated(kind) && (Config["extraFaceOutput"]?.GetValue<bool>() ?? !Legacy)
        && Config[CalibrationSettings.FaceOutputKey(kind)]?.GetValue<bool>() != false;
    string? StockTongue => File.Exists(Path.Combine(Root, "models", "qpro-stereo-tongue-v8-direction.pt")) ? Path.Combine(Root, "models", "qpro-stereo-tongue-v8-direction.pt") : null;
    static string? PersonalTongue(JsonObject config) =>
        config["tongueDirectionModelPath"]?.GetValue<string>() is { Length: >0 } direction && File.Exists(direction) ? direction : null;
    internal void DisableUncalibratedOutputs()
    {
        foreach(var option in new[]{"extraFaceOutput","pupilDilation"})
            if(!Calibrated(option) && Config[option]?.GetValue<bool>() == true) Save(option,false);
    }
    internal string Use => Config["use"]?.GetValue<string>() is ("face" or "hands" or "both") and var use ? use
        : Config["hybridHands"]?.GetValue<bool>() == false ? "face" : "both";
    internal bool Face => Use != "hands";
    internal bool Hands => Use != "face";
    internal bool HybridReady => hybridReady && Alive(Hybrid);
    internal bool IndependentGaze => Config["independentGaze"]?.GetValue<bool>() ?? true;
    internal double VergenceGain => Config["vergenceGain"] is JsonValue v && v.TryGetValue<double>(out var n) && double.IsFinite(n) ? Math.Clamp(n, 0, 3) : 1;
    internal string Python => Config["runtimePython"]?.GetValue<string>() is { } python && File.Exists(python) ? python : SetupService.FindPython(Root);
    internal void CancelSetup() { setup?.CancelOperation(); discoveryCancel?.Cancel(); }
    internal void Notify(string state, string detail = "") { State = state; Detail = detail; Changed?.Invoke(state); }
    internal void Save(string key, JsonNode? value)
    {
        var config = Config; config[key] = value;
        Write(ConfigPath, config);
    }
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
    internal static void Write(string path, JsonObject data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path+".tmp", data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(path+".tmp", path, true); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(10); }
        }
    }
    internal static bool Alive(Process? process) { try { return process is not null && !process.HasExited; } catch { return false; } }
    internal bool Running => Alive(Runtime) || handsSession;
    internal bool SettingUp => setup is not null;
    internal async Task Prepare()
    {
        if (Running) throw new IOException("Stop tracking before running setup.");
        setup = new SetupService(Root);
        setup.StageChanged += (title, detail) => { HelpTarget=setup.HelpTarget; HelpCaption=setup.HelpCaption; Progress=setup.StageProgress; Notify(title,detail); };
        try { await setup.SetupAsync(Config["connectionMode"]?.GetValue<string>()??"auto",
            Config["headsetSerial"]?.GetValue<string>()??"", Config["installModule"]?.GetValue<bool>()??true, Use); }
        finally { setup.Dispose(); setup=null; }
        Save("studioApp",Environment.ProcessPath);
    }
    internal async Task<System.Collections.Generic.List<SetupService.Headset>> Discover()
    {
        using var wizard=new SetupService(Root);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await wizard.DiscoverAsync(timeout.Token);
    }
    internal async Task Start(bool fromModule = false)
    {
        if (Running || (fromModule && attempted)) return;
        var use = Use;
        if (fromModule && use == "hands") return;
        attempted = true;
        if (use == "hands" && SteamVr.IsSteamLink)
            throw new IOException("Hand tracking needs Virtual Desktop, and SteamVR is using Steam Link. Connect with Virtual Desktop, or choose face tracking in Settings.");
        var oldRoot = Config["root"]?.GetValue<string>();
        if (oldRoot is not null)
        {
            var status = Read(Path.Combine(oldRoot, "autostart-status.json"));
            if (status["state"]?.GetValue<string>() is "running" or "starting" or "waiting")
            {
                try { using var previous = Process.GetProcessById(status["pid"]!.GetValue<int>()); if (!previous.HasExited) throw new IOException("Stop the previous tracking app before starting this test build."); }
                catch (ArgumentException) { }
            }
        }
        if (!fromModule && (Config["setupOnStart"]?.GetValue<bool>()??true))
        {
            await Prepare();
        }
        else if (fromModule && (Config["setupOnStart"]?.GetValue<bool>()??true))
        {
            setup=new SetupService(Root);
            try
            {
                discoveryCancel=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var found=await setup.ConnectQuestAsync(discoveryCancel.Token,Config["connectionMode"]?.GetValue<string>()??"auto",Config["headsetSerial"]?.GetValue<string>()??"");
                Save("adbTarget",found.Target);
            }
            finally {setup.Dispose();setup=null;discoveryCancel?.Dispose();discoveryCancel=null;}
        }
        DisableUncalibratedOutputs();
        SteamVr.ConfigureSteamLink();
        var config = Config;
        if (!string.Equals(config["root"]?.GetValue<string>(), Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Complete setup in this app before enabling auto-start.");
        File.Delete(Path.Combine(Root, ".qpro-manual.stop"));
        HybridProblem = "";
        if (File.Exists(Path.Combine(Root, "platform-tools/adb.exe")))
            await AdbServer.EnsureAsync(Path.Combine(Root, "platform-tools/adb.exe"), config["adbTarget"]?.GetValue<string>());
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
        stop = Path.Combine(Root, ".qpro-studio-"+Guid.NewGuid().ToString("N")+".stop");
        var args = new System.Collections.Generic.List<string> { "-u", Path.Combine(Root,"autostart_runtime.py"), "--owner-pid", Environment.ProcessId.ToString(), "--stop-file", stop, "--quiet" };
        if (!IndependentGaze) args.Add("--no-independent-gaze");
        args.AddRange(["--vergence-gain", VergenceGain.ToString(CultureInfo.InvariantCulture)]);
        Add("adbTarget", "--adb-target");
        if (Legacy && (PersonalTongue(config) ?? StockTongue) is { } direction) args.AddRange(["--tongue-model", direction]);
        if (config["pupilDilation"]?.GetValue<bool>() == true) args.Add("--pupil-dilation");
        void Add(string name,string option) { if (config[name]?.GetValue<string>() is { Length: >0 } value) { args.Add(option); args.Add(value); } }
        Runtime?.Dispose(); Runtime = Launch(Python, args.ToArray());
        Notify("Connecting", "Starting the headset cameras…");
        if (!fromModule && (config["openVrApps"]?.GetValue<bool>()??true)) OpenVrApps(face: true);
        hybridPending = use == "both" && !SteamVr.IsSteamLink;
    }
    static void OpenVrApps(bool face)
    {
        var streamers=Process.GetProcessesByName("VirtualDesktop.Streamer");
        foreach(var process in streamers)process.Dispose();
        if(!SteamVr.IsSteamLink&&streamers.Length==0&&File.Exists(SetupService.VirtualDesktopStreamer))Process.Start(new ProcessStartInfo(SetupService.VirtualDesktopStreamer){UseShellExecute=true});
        OpenSteam("250820", "vrserver");
        if (face) OpenSteam("3329480", "VRCFaceTracking");
    }
    internal void Poll()
    {
        if (stopping) return;
        if (Runtime is null && !handsSession) return;
        if (!Running)
        {
            var ended = Read(Path.Combine(Root,"autostart-status.json"));
            var mine = ended["pid"]?.GetValue<int>() == Runtime!.Id;
            ErrorLog = mine && ended["log"]?.GetValue<string>() is { Length: >0 } log ? log : "autostart.log";
            Notify("Tracking stopped", mine && ended["error"]?.GetValue<string>() is { Length: >0 } error ? error : "Tracking ended unexpectedly.");
            return;
        }
        if (Hybrid is not null && !Alive(Hybrid) && Hybrid.ExitCode == 4)
            HybridProblem = "This headset's controller runtime is not supported by hybrid tracking. See the Hybrid log for details.";
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
        var status = Read(Path.Combine(Root,"autostart-status.json"));
        if (status["pid"]?.GetValue<int>() != Runtime!.Id) return;
        switch (status["state"]?.GetValue<string>())
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
                File.WriteAllText(Path.Combine(Root,"hybrid/hybrid.stop"), "stop");
                await Hybrid.WaitForExitAsync();
                cleanupFailed = Hybrid.ExitCode is not (0 or 1 or 4);
                Hybrid.Dispose(); Hybrid = null;
            }
            handsSession = hybridPending = false;
            await StopThumbrest();
            if (Runtime is not null)
            {
                if (stop is not null) File.WriteAllText(stop, "stop");
                await Runtime.WaitForExitAsync(); Runtime.Dispose(); Runtime = null;
            }
            if (cleanupFailed) { HybridProblem = HybridCleanupProblem; Notify("Cleanup needs attention", HybridProblem); }
            else Notify("Ready");
        }
        finally { stopping = false; }
    }
    internal async Task StopThumbrest()
    {
        if (thumbrest is not (Task started, string target)) return;
        thumbrest = null;
        await started; await Thumbrest.StopAsync(this, target);
    }
    internal async Task StartHybrid()
    {
        if (stopping) return;
        if (SteamVr.IsSteamLink) return;
        if (Alive(Hybrid)) return;
        var target = Config["adbTarget"]?.GetValue<string>() ?? "";
        var probe = await Run(Python, ["-c", "import frida; assert frida.__version__ == '17.18.0'"], allowFailure: true);
        if (probe.Code != 0)
        {
            throw new IOException("Hybrid components are missing. Run setup again.");
        }
        using (var headset = new SetupService(Root))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            if (await headset.HandSettingsProblemAsync(target, timeout.Token) is {} problem) throw new IOException(problem);
        var context = new JsonObject { ["adb"] = Path.Combine(Root,"platform-tools/adb.exe"), ["stopFile"] = Path.Combine(Root,"hybrid/hybrid.stop"), ["ownerPid"] = Environment.ProcessId };
        var path = Path.Combine(Root,"hybrid/context.json"); Write(path, context);
        await Run(Python, [Path.Combine(Root,"hybrid/hybrid.py"), "--check", "--target", target]);
        if (stopping) return;
        hybridReady = false; HybridProblem = "";
        Hybrid?.Dispose(); Hybrid = Launch(Python, ["-u", Path.Combine(Root,"hybrid/hybrid.py"), "--target", target], "hybrid.log", path, line => { if (line == "QROOT_READY") hybridReady = true; });
    }
    async Task TryStartHybrid()
    {
        try { await StartHybrid(); }
        catch (Exception error) { HybridProblem = error.Message; try { File.AppendAllText(Path.Combine(Root,"hybrid.log"), "Couldn’t start hybrid hands: "+error.Message+Environment.NewLine); } catch (IOException) {} }
        finally { hybridRestart = null; }
    }
    Process Launch(string exe, string[] args, string? log = null, string? context = null, Action<string>? line = null)
    {
        var info = Info(exe,args);
        if (context is not null) info.Environment["QROOT_CONTEXT"] = context;
        if (log is not null) { info.RedirectStandardOutput = info.RedirectStandardError = true; Rotate(Path.Combine(Root,log)); }
        var process = Process.Start(info) ?? throw new IOException("Could not start tracking");
        if (log is not null)
        {
            _ = Drain(process.StandardOutput); _ = Drain(process.StandardError);
            async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is {} text) { line?.Invoke(text); try { File.AppendAllText(Path.Combine(Root,log),text+Environment.NewLine); } catch (IOException) {} } }
        }
        return process;
    }
    static void Rotate(string path) { try { if (new FileInfo(path) is { Exists: true, Length: > 4 << 20 }) File.Move(path, path + ".old", true); } catch (IOException) {} }
    internal ProcessStartInfo Info(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory=Root, UseShellExecute=false, CreateNoWindow=true };
        foreach(var arg in args) info.ArgumentList.Add(arg);
        info.Environment.Remove("PSModulePath"); info.Environment["QPRO_PYTHON"]=Python;
        info.Environment["QPRO_ADB"]=Path.Combine(Root,"platform-tools/adb.exe"); info.Environment["PYTHONUNBUFFERED"]="1";
        info.Environment["QFT_INFERENCE"]=Config["inferenceDevice"]?.GetValue<string>()??"auto";
        info.Environment["QFT_GPU_INDEX"]=(Config["gpuIndex"]?.GetValue<int>()??0).ToString();
        info.Environment["QFT_GPU_TRAINING"]=Config["gpuTraining"]?.GetValue<bool>()==false?"0":"1";
        return info;
    }
    internal async Task<(int Code,string Output)> Run(string exe,string[] args,bool allowFailure=false,Action<string>? line=null,int seconds=0)
    {
        var info=Info(exe,args); info.RedirectStandardOutput=info.RedirectStandardError=true;
        using var process=Process.Start(info) ?? throw new IOException("Could not start component");
        if(line is not null) try{process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(InvalidOperationException){}
        var output=new StringBuilder();
        async Task Read(StreamReader reader){while(await reader.ReadLineAsync() is {} text){lock(output)output.AppendLine(text);line?.Invoke(text);}}
        using var limit=seconds>0?new CancellationTokenSource(TimeSpan.FromSeconds(seconds)):null;
        try{await Task.WhenAll(Read(process.StandardOutput),Read(process.StandardError),process.WaitForExitAsync(limit?.Token??default));}
        catch(OperationCanceledException){try{process.Kill(true);}catch(InvalidOperationException){}throw new IOException($"{Path.GetFileName(exe)} didn’t answer within {seconds} s.");}
        var text=output.ToString();
        Rotate(Path.Combine(Root,"studio.log")); File.AppendAllText(Path.Combine(Root,"studio.log"),text+Environment.NewLine);
        if(process.ExitCode!=0&&!allowFailure) throw new IOException("Couldn’t finish this step. The Components log in Settings has the details.");
        return (process.ExitCode,text);
    }
    internal static Action<string> TrainingProgress(string kind,Action<double> report)
    {
        int stage=1,stages=1;var prepare=kind=="tongue"?.1:0;
        return line=>
        {
            var match=System.Text.RegularExpressions.Regex.Match(line,@"TRAIN_STAGE index=(\d+) total=(\d+)");
            if(match.Success){stage=int.Parse(match.Groups[1].Value);stages=Math.Max(1,int.Parse(match.Groups[2].Value));report(prepare+(1-prepare)*(stage-1.0)/stages);return;}
            match=System.Text.RegularExpressions.Regex.Match(line,@"TRAIN_EPOCH current=(\d+) total=(\d+)");
            if(match.Success)report(prepare+(1-prepare)*(stage-1+double.Parse(match.Groups[1].Value)/Math.Max(1,double.Parse(match.Groups[2].Value)))/stages);
        };
    }
    internal async Task<string> Train(string kind,string prefix,Action<double>? progress=null)
    {
        if(kind=="pupils") return prefix+".pupils.json";
        if(kind=="enroll")
        {
            var anchors=await Run(Python,[Path.Combine(Root,"guided_session.py"),"check",prefix],allowFailure:true);
            if(anchors.Code!=0) throw new IOException(anchors.Output.Trim());
            return prefix+".anchors.npz";
        }
        var line=TrainingProgress(kind,progress??(_=>{}));
        if(CalibrationSettings.IsFaceGroup(kind))
        {
            var model=Path.Combine(Root,"models",Path.GetFileName(prefix)+".pt");
            await Run(Python,[Path.Combine(Root,"train_extra_face.py"),prefix+".qpcap","--output",model,"--never-puff"],line:line);
            var check=await Run(Python,[Path.Combine(Root,"calibration_ui.py"),"check",model],allowFailure:true);
            if(check.Code!=0) throw new IOException(check.Output.Trim());
            return model;
        }
        var args=new System.Collections.Generic.List<string>{"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(Root,"train-latest-tongue-refinement.ps1"),"-SessionPath",prefix+".qpsession.json"};
        var cfg=Config;
        if(PersonalTongue(cfg) is { } direction) args.AddRange(["-BaseDirectionPath",direction]);
        var output=await Run("powershell.exe",args.ToArray(),line:line);
        var match=System.Text.RegularExpressions.Regex.Match(output.Output,@"MODEL_READY version=(\d+)");
        if(!match.Success) throw new IOException("Creating the calibration didn’t finish. Try calibrating again.");
        return Path.Combine(Root,"models","qpro-stereo-tongue-v"+match.Groups[1].Value);
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
    internal async Task Apply(string kind,string result)
    {
        if(CalibrationSettings.IsFaceGroup(kind)) await Run(Python,[Path.Combine(Root,"calibration_ui.py"),"approve",result]);
        CalibrationSettings.SaveCalibration(Root,kind,result,ConfigPath);
    }
    static void OpenSteam(string id,string process)
    {
        var existing=Process.GetProcessesByName(process); foreach(var p in existing) p.Dispose();
        if(existing.Length==0) Process.Start(new ProcessStartInfo("steam://rungameid/"+id){UseShellExecute=true});
    }
}
