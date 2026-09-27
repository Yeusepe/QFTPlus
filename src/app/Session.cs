using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    EasySetupForm? setup;
    CancellationTokenSource? discoveryCancel;
    internal string State = "Ready", Detail = "";
    internal int Progress;
    internal string? HelpTarget;
    internal string HelpCaption = "Help";
    internal Session(string root, string? configPath = null) { Root = root; ConfigPath = configPath ?? EasySetupForm.AutoPath; }
    internal static JsonObject Read(string path) { try { return CalibrationForm.ReadJson(path); } catch { return new(); } }
    internal JsonObject Config => File.Exists(ConfigPath) ? CalibrationForm.ReadJson(ConfigPath) : new();
    internal string Python => Config["runtimePython"]?.GetValue<string>() is { } python && File.Exists(python) ? python : EasySetupForm.FindPython(Root);
    internal void CancelSetup() { setup?.CancelOperation(); discoveryCancel?.Cancel(); }
    internal void Notify(string state, string detail = "") { State = state; Detail = detail; Changed?.Invoke(state); }
    internal void Save(string key, JsonNode? value)
    {
        var config = Config; config[key] = value;
        Write(ConfigPath, config);
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
        setup = new EasySetupForm(Root);
        setup.StageChanged += (title, detail) => { HelpTarget=setup.HelpTarget; HelpCaption=setup.HelpCaption; Progress=setup.StageProgress; Notify(title,detail); };
        try { await setup.SetupAsync(prepareOnly:true, Config["connectionMode"]?.GetValue<string>()??"auto",
            Config["headsetSerial"]?.GetValue<string>()??"", Config["installModule"]?.GetValue<bool>()??true); }
        finally { setup.Dispose(); setup=null; }
        Save("studioApp",Environment.ProcessPath);
    }
    internal async Task<System.Collections.Generic.List<EasySetupForm.Headset>> Discover()
    {
        using var wizard=new EasySetupForm(Root);
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
            setup=new EasySetupForm(Root);
            try
            {
                discoveryCancel=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var found=await setup.ConnectQuestAsync(discoveryCancel.Token,Config["connectionMode"]?.GetValue<string>()??"auto",Config["headsetSerial"]?.GetValue<string>()??"");
                Save("adbTarget",found.Target);
            }
            finally {setup.Dispose();setup=null;discoveryCancel?.Dispose();discoveryCancel=null;}
        }
        var config = Config;
        if (!string.Equals(config["root"]?.GetValue<string>(), Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Complete setup in this app before enabling auto-start.");
        File.Delete(Path.Combine(Root, ".qpro-manual.stop"));
        stop = Path.Combine(Root, ".qpro-studio-"+Guid.NewGuid().ToString("N")+".stop");
        var args = new System.Collections.Generic.List<string> { "-u", Path.Combine(Root,"autostart_runtime.py"), "--owner-pid", Environment.ProcessId.ToString(), "--stop-file", stop, "--quiet" };
        Add("adbTarget", "--adb-target"); Add("tongueModelPath", "--tongue-model"); Add("tongueDirectionModelPath", "--tongue-direction-model"); Add("extraFaceModel", "--extra-face-model");
        if (config["extraFaceOutput"]?.GetValue<bool>() == true) args.Add("--extra-face-output");
        if (config["pupilDilation"]?.GetValue<bool>() == true) args.Add("--pupil-dilation");
        void Add(string name,string option) { if (config[name]?.GetValue<string>() is { Length: >0 } value) { args.Add(option); args.Add(value); } }
        Runtime = Launch(Python, args.ToArray());
        Notify("Connecting", "Acquiring cameras once…");
        if (!fromModule && (config["openVrApps"]?.GetValue<bool>()??true))
        {
            var vd=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Virtual Desktop Streamer/VirtualDesktop.Streamer.exe");
            var streamers=Process.GetProcessesByName("VirtualDesktop.Streamer");
            foreach(var process in streamers)process.Dispose();
            if(streamers.Length==0&&File.Exists(vd))Process.Start(new ProcessStartInfo(vd){UseShellExecute=true});
            OpenSteam("250820", "vrserver"); OpenSteam("3329480", "VRCFaceTracking");
        }
        if (config["hybridHands"]?.GetValue<bool>() == true)
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
        if (!Running) { Notify("Needs attention", "Tracking stopped. Choose Start when you’re ready to retry."); return; }
        var status = Read(Path.Combine(Root,"autostart-status.json"));
        if (status["pid"]?.GetValue<int>() != Runtime.Id) return;
        switch (status["state"]?.GetValue<string>())
        {
            case "running": Notify("Connected", "Quest Pro · "+((Config["adbTarget"]?.GetValue<string>()??"").Contains(':')?"Wi-Fi":"USB")); break;
            case "waiting": Notify("Waiting for headset", "Connect to this PC in Virtual Desktop and enter SteamVR."); break;
            case "starting": Notify("Connecting", "Acquiring cameras once…"); break;
        }
    }
    internal async Task Stop()
    {
        Notify("Stopping", "Restoring headset tracking…");
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
        Hybrid = Launch(Python, ["-u", Path.Combine(Root,"hybrid/hybrid.py"), "--target", target], "hybrid.log", path);
    }
    internal async Task SetHybrid(bool enabled)
    {
        if (enabled && Running) await StartHybrid();
        if (!enabled && Hybrid is not null) { File.WriteAllText(Path.Combine(Root,"hybrid/hybrid.stop"), "stop"); await Hybrid.WaitForExitAsync(); Hybrid.Dispose(); Hybrid=null; }
        Save("hybridHands", JsonValue.Create(enabled));
    }
    Process Launch(string exe, string[] args, string? log = null, string? context = null)
    {
        var info = Info(exe,args);
        if (context is not null) info.Environment["QROOT_CONTEXT"] = context;
        if (log is not null) { info.RedirectStandardOutput = info.RedirectStandardError = true; }
        var process = Process.Start(info) ?? throw new IOException("Could not start tracking");
        if (log is not null)
        {
            _ = Drain(process.StandardOutput); _ = Drain(process.StandardError);
            async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is {} line) { try { File.AppendAllText(Path.Combine(Root,log),line+Environment.NewLine); } catch (IOException) {} } }
        }
        return process;
    }
    internal ProcessStartInfo Info(string exe, string[] args)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory=Root, UseShellExecute=false, CreateNoWindow=true };
        foreach(var arg in args) info.ArgumentList.Add(arg);
        info.Environment.Remove("PSModulePath"); info.Environment["QPRO_PYTHON"]=Python;
        info.Environment["QPRO_ADB"]=Path.Combine(Root,"platform-tools/adb.exe"); info.Environment["PYTHONUNBUFFERED"]="1";
        info.Environment["QFT_INFERENCE"]=Config["inferenceDevice"]?.GetValue<string>()??"auto";
        info.Environment["QFT_GPU_INDEX"]=(Config["gpuIndex"]?.GetValue<int>()??0).ToString();
        return info;
    }
    internal async Task<(int Code,string Output)> Run(string exe,string[] args,bool allowFailure=false)
    {
        var info=Info(exe,args); info.RedirectStandardOutput=info.RedirectStandardError=true;
        using var process=Process.Start(info) ?? throw new IOException("Could not start component");
        var output=process.StandardOutput.ReadToEndAsync(); var errors=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var text=(await output)+(await errors);
        File.AppendAllText(Path.Combine(Root,"studio.log"),text+Environment.NewLine);
        if(process.ExitCode!=0&&!allowFailure) throw new IOException("Couldn’t finish this step. Details are in studio.log.\n"+text[^Math.Min(700,text.Length)..]);
        return (process.ExitCode,text);
    }
    internal async Task<string> Train(string kind,string prefix)
    {
        Notify("Learning", "Your current tracking continues.");
        if(kind=="pupils") return prefix+".pupils.json";
        if(kind=="puff")
        {
            var model=Path.Combine(Root,"models",Path.GetFileName(prefix)+".pt");
            await Run(Python,[Path.Combine(Root,"train_extra_face.py"),prefix+".qpcap","--output",model]);
            await Run(Python,[Path.Combine(Root,"calibration_ui.py"),"check-puff",model]);
            return model;
        }
        var args=new System.Collections.Generic.List<string>{"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(Root,"train-latest-tongue-refinement.ps1"),"-SessionPath",prefix+".qpsession.json"};
        var cfg=Config;
        if(cfg["tongueModelPath"]?.GetValue<string>() is {} gate && cfg["tongueDirectionModelPath"]?.GetValue<string>() is {} direction) args.AddRange(["-BaseGatePath",gate,"-BaseDirectionPath",direction]);
        var output=await Run("powershell.exe",args.ToArray());
        var match=System.Text.RegularExpressions.Regex.Match(output.Output,@"MODEL_READY version=(\d+)");
        if(!match.Success) throw new IOException("No complete model pair was produced.");
        return Path.Combine(Root,"models","qpro-stereo-tongue-v"+match.Groups[1].Value);
    }
    internal async Task Apply(string kind,string result)
    {
        if(kind=="puff") await Run(Python,[Path.Combine(Root,"calibration_ui.py"),"approve-puff",result]);
        CalibrationForm.SaveCalibration(Root,kind,result,ConfigPath);
    }
    static void OpenSteam(string id,string process)
    {
        var existing=Process.GetProcessesByName(process); foreach(var p in existing) p.Dispose();
        if(existing.Length==0) Process.Start(new ProcessStartInfo("steam://rungameid/"+id){UseShellExecute=true});
    }
}
