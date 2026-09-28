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
    DateTime hybridRetry;
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
        "extraFaceOutput" => CalibrationSettings.FaceGroups.Any(group => FaceCalibrated(group.Kind)),
        "pupilDilation" => Read(Path.Combine(Root,"calibration/qpro-pupil-dilation.json"))["format"]?.GetValue<string>() == "qpro-relative-pupil-v1",
        _ => true
    };
    internal bool FaceCalibrated(string kind) => File.Exists(Config[CalibrationSettings.FaceModelKey(kind)]?.GetValue<string>());
    internal bool FaceOn(string kind) => FaceCalibrated(kind) && Config["extraFaceOutput"]?.GetValue<bool>() == true
        && Config[CalibrationSettings.FaceOutputKey(kind)]?.GetValue<bool>() != false;
    internal void DisableUncalibratedOutputs()
    {
        foreach(var option in new[]{"extraFaceOutput","pupilDilation"})
            if(!Calibrated(option) && Config[option]?.GetValue<bool>() == true) Save(option,false);
    }
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
        File.Move(path+".tmp", path, true);
    }
    internal static bool Alive(Process? process) { try { return process is not null && !process.HasExited; } catch { return false; } }
    internal bool Running => Alive(Runtime);
    internal bool SettingUp => setup is not null;
    internal async Task Prepare()
    {
        if (Running) throw new IOException("Stop tracking before running setup.");
        setup = new SetupService(Root);
        setup.StageChanged += (title, detail) => { HelpTarget=setup.HelpTarget; HelpCaption=setup.HelpCaption; Progress=setup.StageProgress; Notify(title,detail); };
        try { await setup.SetupAsync(Config["connectionMode"]?.GetValue<string>()??"auto",
            Config["headsetSerial"]?.GetValue<string>()??"", Config["installModule"]?.GetValue<bool>()??true); }
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
        attempted = true;
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
        stop = Path.Combine(Root, ".qpro-studio-"+Guid.NewGuid().ToString("N")+".stop");
        var args = new System.Collections.Generic.List<string> { "-u", Path.Combine(Root,"autostart_runtime.py"), "--owner-pid", Environment.ProcessId.ToString(), "--stop-file", stop, "--quiet" };
        if (!IndependentGaze) args.Add("--no-independent-gaze");
        args.AddRange(["--vergence-gain", VergenceGain.ToString(CultureInfo.InvariantCulture)]);
        Add("adbTarget", "--adb-target"); Add("tongueModelPath", "--tongue-model"); Add("tongueDirectionModelPath", "--tongue-direction-model");
        if (config["pupilDilation"]?.GetValue<bool>() == true) args.Add("--pupil-dilation");
        void Add(string name,string option) { if (config[name]?.GetValue<string>() is { Length: >0 } value) { args.Add(option); args.Add(value); } }
        Runtime?.Dispose(); Runtime = Launch(Python, args.ToArray());
        Notify("Connecting", "Starting the headset cameras…");
        if (!fromModule && (config["openVrApps"]?.GetValue<bool>()??true))
        {
            var vd=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Virtual Desktop Streamer/VirtualDesktop.Streamer.exe");
            var streamers=Process.GetProcessesByName("VirtualDesktop.Streamer");
            foreach(var process in streamers)process.Dispose();
            if(!SteamVr.IsSteamLink&&streamers.Length==0&&File.Exists(vd))Process.Start(new ProcessStartInfo(vd){UseShellExecute=true});
            OpenSteam("250820", "vrserver"); OpenSteam("3329480", "VRCFaceTracking");
        }
        if (config["hybridHands"]?.GetValue<bool>() == true && !SteamVr.IsSteamLink)
        {
            var deadline=DateTime.UtcNow.AddSeconds(90);
            while(Running && DateTime.UtcNow<deadline)
            {
                var status=Read(Path.Combine(Root,"autostart-status.json"));
                if(status["pid"]?.GetValue<int>()==Runtime.Id && status["state"]?.GetValue<string>()=="running") { await StartHybrid(); break; }
                await Task.Delay(1000);
            }
        }
    }
    internal void Poll()
    {
        if (Runtime is null) return;
        if (!Running)
        {
            var ended = Read(Path.Combine(Root,"autostart-status.json"));
            var mine = ended["pid"]?.GetValue<int>() == Runtime.Id;
            ErrorLog = mine && ended["log"]?.GetValue<string>() is { Length: >0 } log ? log : "autostart.log";
            Notify("Tracking stopped", mine && ended["error"]?.GetValue<string>() is { Length: >0 } error ? error : "Tracking ended unexpectedly.");
            return;
        }
        if (Hybrid is not null && !Alive(Hybrid) && hybridRestart is null && DateTime.UtcNow >= hybridRetry
            && Config["hybridHands"]?.GetValue<bool>() == true)
        {
            hybridRetry = DateTime.UtcNow.AddSeconds(15);
            hybridRestart = RestartHybrid();
        }
        var status = Read(Path.Combine(Root,"autostart-status.json"));
        if (status["pid"]?.GetValue<int>() != Runtime.Id) return;
        switch (status["state"]?.GetValue<string>())
        {
            case "running": Notify("Connected", "Quest Pro · "+((Config["adbTarget"]?.GetValue<string>()??"").Contains(':')?"Wi-Fi":"USB")); break;
            case "waiting": Notify("Waiting for headset", "Connect to this PC in Steam Link or Virtual Desktop and enter SteamVR."); break;
            case "starting": Notify("Connecting", "Starting the headset cameras…"); break;
        }
    }
    internal async Task Stop()
    {
        Notify("Stopping", "Restoring headset tracking…");
        if (hybridRestart is not null) await hybridRestart;
        if (Hybrid is not null) { File.WriteAllText(Path.Combine(Root,"hybrid/hybrid.stop"), "stop"); await Hybrid.WaitForExitAsync(); Hybrid.Dispose(); Hybrid = null; }
        if (Runtime is not null)
        {
            if (stop is not null) File.WriteAllText(stop, "stop");
            await Runtime.WaitForExitAsync(); Runtime.Dispose(); Runtime = null;
        }
        Notify("Ready");
    }
    internal async Task StartHybrid()
    {
        if (SteamVr.IsSteamLink) return;
        if (Alive(Hybrid)) return;
        var target = Config["adbTarget"]?.GetValue<string>() ?? "";
        var probe = await Run(Python, ["-c", "import frida; assert frida.__version__ == '17.18.0'"], allowFailure: true);
        if (probe.Code != 0)
        {
            throw new IOException("Hybrid components are missing. Run setup again.");
        }
        var context = new JsonObject { ["adb"] = Path.Combine(Root,"platform-tools/adb.exe"), ["stopFile"] = Path.Combine(Root,"hybrid/hybrid.stop"), ["ownerPid"] = Environment.ProcessId };
        var path = Path.Combine(Root,"hybrid/context.json"); Write(path, context);
        await Run(Python, [Path.Combine(Root,"hybrid/hybrid.py"), "--check", "--target", target]);
        Hybrid?.Dispose(); Hybrid = Launch(Python, ["-u", Path.Combine(Root,"hybrid/hybrid.py"), "--target", target], "hybrid.log", path);
    }
    async Task RestartHybrid()
    {
        try { await StartHybrid(); }
        catch (Exception error) { try { File.AppendAllText(Path.Combine(Root,"hybrid.log"), "Restart failed: "+error.Message+Environment.NewLine); } catch (IOException) {} }
        finally { hybridRestart = null; }
    }
    internal async Task SetHybrid(bool enabled)
    {
        if (hybridRestart is not null) await hybridRestart;
        if (enabled && Running) await StartHybrid();
        if (!enabled && Hybrid is not null) { File.WriteAllText(Path.Combine(Root,"hybrid/hybrid.stop"), "stop"); await Hybrid.WaitForExitAsync(); Hybrid.Dispose(); Hybrid=null; }
        Save("hybridHands", JsonValue.Create(enabled));
    }
    Process Launch(string exe, string[] args, string? log = null, string? context = null)
    {
        var info = Info(exe,args);
        if (context is not null) info.Environment["QROOT_CONTEXT"] = context;
        if (log is not null) { info.RedirectStandardOutput = info.RedirectStandardError = true; Rotate(Path.Combine(Root,log)); }
        var process = Process.Start(info) ?? throw new IOException("Could not start tracking");
        if (log is not null)
        {
            _ = Drain(process.StandardOutput); _ = Drain(process.StandardError);
            async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is {} line) { try { File.AppendAllText(Path.Combine(Root,log),line+Environment.NewLine); } catch (IOException) {} } }
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
    internal async Task<(int Code,string Output)> Run(string exe,string[] args,bool allowFailure=false,Action<string>? line=null)
    {
        var info=Info(exe,args); info.RedirectStandardOutput=info.RedirectStandardError=true;
        using var process=Process.Start(info) ?? throw new IOException("Could not start component");
        if(line is not null) try{process.PriorityClass=ProcessPriorityClass.BelowNormal;}catch(InvalidOperationException){}
        var output=new StringBuilder();
        async Task Read(StreamReader reader){while(await reader.ReadLineAsync() is {} text){lock(output)output.AppendLine(text);line?.Invoke(text);}}
        await Task.WhenAll(Read(process.StandardOutput),Read(process.StandardError),process.WaitForExitAsync());var text=output.ToString();
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
        var line=TrainingProgress(kind,progress??(_=>{}));
        if(CalibrationSettings.IsFaceGroup(kind))
        {
            var model=Path.Combine(Root,"models",Path.GetFileName(prefix)+".pt");
            await Run(Python,[Path.Combine(Root,"train_extra_face.py"),prefix+".qpcap","--output",model],line:line);
            var check=await Run(Python,[Path.Combine(Root,"calibration_ui.py"),"check",model],allowFailure:true);
            if(check.Code!=0) throw new IOException(check.Output.Trim());
            return model;
        }
        var args=new System.Collections.Generic.List<string>{"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(Root,"train-latest-tongue-refinement.ps1"),"-SessionPath",prefix+".qpsession.json"};
        var cfg=Config;
        if(cfg["tongueModelPath"]?.GetValue<string>() is {} gate && cfg["tongueDirectionModelPath"]?.GetValue<string>() is {} direction) args.AddRange(["-BaseGatePath",gate,"-BaseDirectionPath",direction]);
        var output=await Run("powershell.exe",args.ToArray(),line:line);
        var match=System.Text.RegularExpressions.Regex.Match(output.Output,@"MODEL_READY version=(\d+)");
        if(!match.Success) throw new IOException("Creating the calibration didn’t finish. Try calibrating again.");
        return Path.Combine(Root,"models","qpro-stereo-tongue-v"+match.Groups[1].Value);
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
