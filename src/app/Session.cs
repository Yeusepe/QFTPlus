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
    internal string ConfigPath;
    internal event Action<string>? Changed;
    internal Process? Hybrid;
    internal Tracking? tracking;
    bool attempted;
    internal SetupService? setup;
    CancellationTokenSource cancelStart = new();
    internal Task? hybridRestart;
    internal (Task<HeadsetCleanup?> Started, string Target)? thumbrest;
    internal DateTime hybridRetry;
    internal bool handsSession;
    bool headsetSession;
    internal JsonObject? App;
    internal string AppProblem = "";
    Task? appPoll;
    DateTime appPolled;
    int appVersion;
    bool? appWanted;
    readonly SemaphoreSlim appGate = new(1, 1);
    int notRunning;

    internal async Task<JsonObject> SendApp(CancellationToken token, params (string Key, object Value)[] changes)
    {
        await appGate.WaitAsync(token);
        try
        {
            var wanted = appWanted;
            JsonObject reply;
            try { reply = await HeadsetApp.SendAsync(this, token, wanted is { } want ? [("tracking", want), .. changes] : changes); }
            catch (HeadsetApp.Refused) { if (appWanted == wanted) appWanted = null; throw; }
            if (appWanted == wanted) appWanted = null;
            return reply;
        }
        finally { appGate.Release(); }
    }
    volatile bool hybridReady;
    bool hybridPending, stopping;
    string lastSlow = "";
    static string HybridCleanupProblem => Loc.Get("HybridTrackingCouldNotConfirmCleanupRestartVirtualDesktopOn");
    internal string HybridProblem = "";
    volatile string? hybridIncompatible;
    internal string State = "Ready", Detail = "";
    internal string StateTitle => Loc.Get(State);
    internal bool Wireless => (Config["adbTarget"]?.GetValue<string>() ?? "").Contains(':');
    internal int Progress;
    internal string? HelpTarget;
    internal string HelpCaption = Loc.Get("Help");
    internal string ErrorLog = "tracking.log";
    internal Session(string root, string? configPath = null) { Root = root; ConfigPath = configPath ?? SetupService.AutoPath; }
    internal static JsonObject Read(string path)
    {
        try { return CalibrationSettings.ReadJson(path); }
        catch (JsonException error)
        { throw new InvalidDataException(Loc.Format("IsDamagedQFTLeftItAsItIsDeleteIt", Path.GetFileName(path), error.Message), error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidDataException(Loc.Format("QFTCouldnTReadTryAgainInAMoment", Path.GetFileName(path), error.Message), error); }
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
    internal bool IndependentGaze => !HeadsetModelExperiment.Standalone(this) && (Config["independentGaze"]?.GetValue<bool>() ?? true);
    internal double VergenceGain => Config["vergenceGain"] is JsonValue v && v.TryGetValue<double>(out var n) && double.IsFinite(n) ? Math.Clamp(n, 0, 3) : 1;
    internal string Python => PythonRuntime.Exe(Root);
    internal void CancelSetup() { setup?.CancelOperation(); cancelStart.Cancel(); }
    internal async Task SetHeadsetModel(bool enabled)
    {
        cancelStart = new();
        await HeadsetModelExperiment.ConfigureAsync(this, enabled, cancelStart.Token);
    }
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
        catch (SocketException error) { throw new IOException(Loc.Get("YourChangeWasSavedStopAndStartTrackingToApply"), error); }
    }
    internal static bool Alive(Process? process) { try { return process is not null && !process.HasExited; } catch { return false; } }
    internal bool Running => tracking?.Running == true || handsSession || headsetSession;
    internal bool SettingUp => setup is not null;
    internal async Task Prepare()
    {
        if (Running) throw new IOException(Loc.Get("StopTrackingBeforeRunningSetup"));
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
        if (stopTask is { IsCompleted: false } stop) await stop;
        if (Running || (fromModule && attempted)) return;
        var use = Use;
        if (fromModule && use == "hands") return;
        attempted = true;
        var token = (cancelStart = new()).Token;
        if (HeadsetModelExperiment.Standalone(this))
        {
            if (HeadsetModelExperiment.AppChanged(this)) await HeadsetModelExperiment.ConfigureAsync(this, true, token);
            Save("headsetReceiverEnabled", true);
            headsetSession = true;
            if (!fromModule && (Config["openVrApps"]?.GetValue<bool>() ?? true)) OpenVrApps(face: true);
            appVersion++;
            Notify("Connecting", Loc.Get("TurningOnTrackingInQFTHeadset"));
            appWanted = true;
            try { (App, AppProblem) = (await SendApp(token), ""); }
            catch (IOException error) { AppProblem = error.Message; }
            catch (OperationCanceledException) { (headsetSession, appWanted) = (false, null); Save("headsetReceiverEnabled", false); throw; }
            try
            {
                StartThumbrest();
                if (Hands && !SteamVr.IsSteamLink)
                {
                    await Adb.EnsureAsync(Adb.Exe(Root), Config["adbTarget"]?.GetValue<string>(), token);
                    await TryStartHybrid();
                }
            }
            catch { await Stop(); throw; }
            NotifyApp();
            return;
        }
        await StopThumbrest();
        if (use == "hands" && SteamVr.IsSteamLink)
            throw new IOException(Loc.Get("HandTrackingNeedsVirtualDesktopAndSteamVRIsUsingSteam"));
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
            throw new IOException(Loc.Get("CompleteSetupInThisAppBeforeEnablingAutoStart"));
        HybridProblem = "";
        if (!PythonRuntime.Ready(Root)) { Notify("PreparingComponents", Loc.Get("OneTimeSetup")); await PythonRuntime.EnsureAsync(Root, token); }
        await Adb.EnsureAsync(Adb.Exe(Root), config["adbTarget"]?.GetValue<string>(), token);
        await HeadsetModelExperiment.EnsureAsync(this, config["adbTarget"]?.GetValue<string>() ?? "", token);
        StartThumbrest();
        if (use == "hands")
        {
            handsSession = true;
            Notify("Connecting", Loc.Get("StartingHandTracking"));
            if (config["openVrApps"]?.GetValue<bool>()??true) OpenVrApps(face: false);
            try { await StartHybrid(); }
            catch { handsSession = false; throw; }
            return;
        }
        tracking = new Tracking(this, config["adbTarget"]?.GetValue<string>() ?? "", IndependentGaze, VergenceGain);
        Notify("Connecting", Loc.Get("StartingTheHeadsetCameras"));
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
        if (HeadsetModelExperiment.Standalone(this) && appPoll is not { IsCompleted: false } && DateTime.UtcNow - appPolled > TimeSpan.FromSeconds(2))
            appPoll = RefreshApp();
        if (tracking is null && !handsSession && !headsetSession) return;
        if (!Running)
        {
            ErrorLog = tracking!.ErrorLog;
            Notify("TrackingStopped", tracking.Error.Length > 0 ? tracking.Error : Loc.Get("TrackingEndedUnexpectedly"));
            return;
        }
        if (Hybrid is not null && !Alive(Hybrid) && Hybrid.ExitCode == 4)
            HybridProblem = hybridIncompatible ?? Loc.Get("ThisHeadsetSControllerRuntimeIsNotSupportedByHybrid");
        if (Hybrid is not null && !Alive(Hybrid) && Hybrid.ExitCode is not (0 or 1 or 4))
            HybridProblem = HybridCleanupProblem;
        if (Hybrid is not null && !Alive(Hybrid) && Hybrid.ExitCode is (0 or 1) && hybridRestart is null && DateTime.UtcNow >= hybridRetry && Hands)
        {
            hybridRetry = DateTime.UtcNow.AddSeconds(15);
            hybridRestart = TryStartHybrid();
        }
        var via = Wireless ? Loc.Get("WiFiConnection") : Loc.Get("UsbConnection");
        if (headsetSession) { NotifyApp(); return; }
        if (handsSession)
        {
            if (HybridProblem.Length > 0) Notify("HybridTrackingUnavailable", HybridProblem);
            else if (HybridReady) Notify("Connected", Loc.Format("QuestConnection", via));
            else Notify("WaitingForHeadset", Loc.Get("PutOnTheHeadsetConnectToThisPCInVirtual"));
            return;
        }
        switch (tracking!.State)
        {
            case "running":
                Notify("Connected", Loc.Format("QuestConnection", via));
                var slow = tracking.SlowCause is { Length: > 0 } cause ? cause + tracking.Stream!.Fps.ToString("0") : "";
                if (slow != lastSlow) { lastSlow = slow; Changed?.Invoke(State); }
                if (hybridPending && hybridRestart is null) { hybridPending = false; hybridRestart = TryStartHybrid(); }
                break;
            case "waiting": Notify("WaitingForHeadset", Loc.Get("ConnectToThisPCInSteamLinkOrVirtualDesktop")); break;
            case "starting": Notify("Connecting", Loc.Get("StartingTheHeadsetCameras")); break;
        }
    }
    Task? stopTask;
    internal Task Stop() => stopTask is { IsCompleted: false } ? stopTask : stopTask = StopOnce();
    async Task StopOnce()
    {
        stopping = true;
        try
        {
            Notify("Stopping", Loc.Get("RestoringHeadsetTracking"));
            var appNote = "";
            if (headsetSession)
            {
                appVersion++;
                appWanted = false;
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    (App, AppProblem) = (await SendApp(timeout.Token), "");
                }
                catch (Exception error) when (error is IOException or OperationCanceledException)
                { appNote =Loc.Get("ThisPCStoppedReceivingButTheHeadsetCouldnTBe"); }
                Save("headsetReceiverEnabled", false);
                Save("headsetDirect", false);
                headsetSession = false;
            }
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
            if (cleanupFailed) { HybridProblem = HybridCleanupProblem; Notify("CleanupNeedsAttention", HybridProblem); }
            else Notify("Ready", appNote);
        }
        finally { stopping = false; }
    }
    string syncError = "";
    async Task RefreshApp()
    {
        var version = appVersion;
        var calibration = App?["calibration"]?.ToJsonString();
        appPolled = DateTime.UtcNow;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            App = await SendApp(timeout.Token);
            try { App = await SyncAdjustments(App, timeout.Token); syncError = ""; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                if (error.Message != syncError) Log(Path.Combine(Root, "studio.log"), $"{DateTimeOffset.Now:O} Adjustments sync with the headset failed: {error.Message}");
                syncError = error.Message;
            }
            AppProblem = "";
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        { AppProblem = error is IOException ? error.Message : Loc.Get("TheHeadsetIsnTAnsweringMakeSureItSAwake"); }
        appPolled = DateTime.UtcNow;
        if (version != appVersion || stopping || setup is not null || AppProblem.Length > 0) { Changed?.Invoke(State); return; }
        var on = App?["tracking"]?.GetValue<bool>() == true;
        notRunning = on && App?["running"]?.GetValue<bool>() == false ? notRunning + 1 : 0;
        var closed = notRunning >= 2;
        if (headsetSession && (!on || closed))
        {
            var error = closed ? Loc.Get("QFTHeadsetClosedUnexpectedlyStartTrackingAgain") : App?["error"]?.GetValue<string>() ?? "";
            await Stop();
            if (HybridProblem.Length == 0)
                Notify(error.Length > 0 ? "TrackingStopped" : "Ready", error.Length > 0 ? error : Loc.Get("TrackingWasTurnedOffInQFTHeadset"));
            return;
        }
        var direct = on && HeadsetApp.Destination(App).Length > 0 && App?["settings"]?["oscHost"]?.GetValue<string>() is { Length: > 0 };
        if (direct != (Config["headsetDirect"]?.GetValue<bool>() == true)) Save("headsetDirect", direct);
        if (on && !closed && !headsetSession)
        {
            headsetSession = true;
            Save("headsetReceiverEnabled", true);
            if (Config["openVrApps"]?.GetValue<bool>() ?? true) OpenVrApps(face: true);
        }
        if (headsetSession) NotifyApp();
        if (!headsetSession || App?["calibration"]?.ToJsonString() != calibration) Changed?.Invoke(State);
    }
    async Task<JsonObject> SyncAdjustments(JsonObject app, CancellationToken token)
    {
        if (app["adjustments"] is not JsonValue reported || !reported.TryGetValue(out string? headset)) return app;
        var path = Path.Combine(Root, "output-settings.json");
        var local = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var pc = local is null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(local)).ToLowerInvariant();
        var synced = Config["headsetAdjustmentsHash"]?.GetValue<string>();
        if (pc == headset)
        {
            if (synced != pc) Save("headsetAdjustmentsHash", pc);
            return app;
        }
        if (headset.Length > 0 && headset != synced)
        {
            var reply = await SendApp(token, ("adjustmentsWanted", true));
            byte[] data;
            try { data = Convert.FromBase64String(reply["adjustmentsData"]?.GetValue<string>() ?? ""); }
            catch (FormatException error) { throw new IOException(HeadsetApp.Outdated, error); }
            if (data.Length == 0) return reply;
            var pending = path + ".tmp";
            File.WriteAllBytes(pending, data);
            File.Move(pending, path, true);
            Save("headsetAdjustmentsHash", headset);
            return reply;
        }
        if (local is null) return app;
        var pushed = await SendApp(token, ("adjustments", Convert.ToBase64String(local)));
        Save("headsetAdjustmentsHash", pc);
        return pushed;
    }
    void NotifyApp()
    {
        var status = App?["status"]?.GetValue<string>() ?? "";
        if (AppProblem.Length > 0) Notify("WaitingForHeadset", AppProblem);
        else if (App?["pcConnected"]?.GetValue<bool>() == true) Notify("Connected", status);
        else if (App?["receiving"]?.GetValue<bool>() == true && HeadsetApp.Destination(App) is { Length: > 0 } destination)
            Notify("Connected", Loc.Format("StatusDetail", status, destination));
        else if (App?["receiving"]?.GetValue<bool>() == true)
            Notify("Connecting", Loc.Get("QFTHeadsetIsTrackingWaitingForVRCFaceTrackingOnThisPC"));
        else Notify("Connecting", status.Length > 0 ? status : Loc.Get("StartingQFTHeadset"));
    }
    void StartThumbrest()
    {
        if (Config["steamvrDriver"]?.GetValue<bool>() == true && Config["adbTarget"]?.GetValue<string>() is { Length: > 0 } target)
            thumbrest = (Thumbrest.StartAsync(this, target), target);
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
        var temp = OwnTemp(info);
        var logPath = Path.Combine(Root, log);
        Rotate(logPath, clear: log != "hybrid.log");
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.Exited += (_, _) => _ = DeleteTemp(temp);
        if (!process.Start()) throw new IOException(Loc.Format("CouldNotStart", Path.GetFileName(exe)));
        if (log == "hybrid.log") Log(logPath, $"{DateTimeOffset.Now:O} Starting hybrid hands (PID {process.Id}).");
        var stdout = process.StandardOutput;
        var stderr = process.StandardError;
        _ = Task.Run(() => Drain(stdout)); _ = Task.Run(() => Drain(stderr));
        async Task Drain(StreamReader reader) { while (await reader.ReadLineAsync() is {} text) { line?.Invoke(text); Log(logPath,text); } }
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
    internal static string Temp(string root) => Path.Combine(root, "temp");
    string OwnTemp(ProcessStartInfo info)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Temp(Root), Guid.NewGuid().ToString("N")[..8])).FullName;
        info.Environment["TEMP"] = info.Environment["TMP"] = folder;
        return folder;
    }
    static async Task DeleteTemp(string folder)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await Task.Delay(1000); }
        }
    }
    internal static void ClearTemp(string root)
    {
        if (!Directory.Exists(Temp(root))) return;
        foreach (var folder in Directory.GetDirectories(Temp(root)))
            try { Directory.Delete(folder, true); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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
        var info = Info(exe,args); var temp = OwnTemp(info);
        var (code,text)=await Processes.RunAsync(info,default,seconds);
        _ = DeleteTemp(temp);
        Rotate(Path.Combine(Root,"studio.log")); Log(Path.Combine(Root,"studio.log"),text);
        if(code!=0&&!allowFailure) throw new IOException(Loc.Get("CouldnTFinishThisStepTheComponentsLogInSettings"));
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
        var consent=Config["testerConsent"]??throw new InvalidOperationException(Loc.Get("BecomeATesterFirst"));
        var args=new System.Collections.Generic.List<string>{Path.Combine(Root,"contribution.py"),prefix,"--tester",consent["tester"]!.GetValue<string>(),
            "--consent-version",consent["version"]!.GetValue<int>().ToString(),"--consent-time",consent["time"]!.GetValue<string>()};
        if(Config["faceEnrollment"]?.GetValue<string>() is {Length:>0} enrollment&&File.Exists(enrollment))args.AddRange(["--enrollment",enrollment]);
        if(Updates.Installed() is {} version)args.AddRange(["--version",version.ToString()]);
        var run=await Run(Python,args.ToArray(),allowFailure:true);
        var match=System.Text.RegularExpressions.Regex.Match(run.Output,@"^PACKAGE (.+) \d+\s*$",System.Text.RegularExpressions.RegexOptions.Multiline);
        if(run.Code!=0||!match.Success)throw new IOException(Loc.Format("CouldnTPrepareTheRecordingToShare", run.Output.Trim().Split('\n').LastOrDefault()));
        return match.Groups[1].Value.Trim();
    }
    internal void Apply(string kind,string result) { CalibrationSettings.SaveCalibration(Root,kind,result,ConfigPath); Saved(); }
    static void OpenSteam(string id,string process)
    {
        if(!Processes.Running(process)) Process.Start(new ProcessStartInfo("steam://rungameid/"+id){UseShellExecute=true})?.Dispose();
    }
}
