using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace QFTPlus;

internal sealed class SetupService(string root)
{
    private CancellationTokenSource? _operation;
    private string? _stage;
    internal static string AutoPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "QproAutoStart.json");
    internal static string InstalledModule => Path.Combine(Path.GetDirectoryName(AutoPath)!, "CustomLibs", "000-Qpro.IndependentGaze.dll");
    private string adb => Adb.Exe(root);
    private string LogPath => Path.Combine(root, "setup.log");
    internal event Action<string, string>? StageChanged;
    internal void CancelOperation() => _operation?.Cancel();
    internal string? HelpTarget { get; private set; }
    internal string HelpCaption { get; private set; } = Loc.Get("Help");
    internal int StageProgress { get; private set; }

    private void Stage(string title, string text, int progress, string? help = null, string? helpText = null)
    {
        if (_stage != title) Log(title);
        _stage = title;
        StageProgress = progress;
        HelpTarget = help;
        HelpCaption = helpText ?? Loc.Get("Help");
        StageChanged?.Invoke(title, text);
    }

    internal async Task SetupAsync(string connectionMode = "auto", string preferredSerial = "", bool installModule = true, string use = "both")
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        var token = operation.Token;
        var face = use != "hands";
        try
        {
            var source = ModuleSource(root);
            var installed = InstalledModule;
            if (face) ValidateModuleFiles(root, installModule);
            foreach (var file in new[] { adb, Path.Combine(root, "receiver.py") })
                if (!File.Exists(file)) throw new IOException(Loc.Get("TheAppIsMissingARequiredFileRunTheComplete"));
            Stage("Connecting", Loc.Get("LookingForYourQuestPro"), 5);
            await Adb.EnsureAsync(adb, token: token);
            var connection = await ConnectQuestAsync(token, connectionMode, preferredSerial);

            if (!PythonRuntime.Ready(root))
            {
                Stage("PreparingComponents", Loc.Get("OneTimeSetup"), 35);
                try { await PythonRuntime.EnsureAsync(root, token); }
                catch (IOException error) when (!token.IsCancellationRequested)
                { Log(error.ToString()); throw new IOException(Loc.Get("ThePCComponentsDidnTFinishInstallingMakeSureThis"), error); }
            }

            var independentGaze = CalibrationSettings.ReadJson(AutoPath)["independentGaze"]?.GetValue<bool>() != false;
            if (!face) Stage("SkippingFaceTracking", Loc.Get("FaceTrackingIsnTSelected"), 55);
            else if (independentGaze)
            {
                Stage("PreparingEyeTracking", Loc.Get("KeepYourHeadsetAwake"), 55);
                if (!File.Exists(EyeModel(root))) await PrepareEyeModelAsync(connection.Target, token);
            }
            else Stage("UsingStandardEyeTracking", Loc.Get("IndependentEyeGazeIsOff"), 55);

            if (face) await WaitForSteamAppAsync("3329480", "VRCFaceTracking", token);
            await WaitForSteamAppAsync("250820", "SteamVR", token);
            if (use == "hands") { await WaitForVirtualDesktopAsync(token); await WaitForHandSettingsAsync(connection.Target, token); }
            if (face && installModule && !SameFile(source, installed))
            {
                Stage("UpdatingTracking", Loc.Get("InstallingTheUpdatedVRCFaceTrackingBridge"), 70);
                if (Processes.Running("VRCFaceTracking")) await CloseVrcftAsync(token);
                await InstallModule(source, installed);
                if (!SameFile(source, installed)) throw new IOException(Loc.Get("TheVRCFaceTrackingModuleDidNotFinishInstallingTryAgainOpen"));
            }
            if (face && (!File.Exists(installed) || new FileInfo(installed).Length == 0))
            {
                Stage("UpdatingTracking", "", 70);
                throw new ModuleNotInstalledException(SteamVr.IsSteamLink
                    ? Loc.Get("FaceTrackingNeedsTheQFTModuleInVRCFaceTrackingAndAutomatic")
                    : Loc.Get("FaceTrackingNeedsTheQFTModuleInVRCFaceTrackingAndAutomaticMessage"));
            }
            SaveAutomaticSetup(root, connection.Target, AutoPath);
            SteamVr.ConfigureSteamLink();
            Stage("Ready", "", 100);
        }
        catch (OperationCanceledException)
        {
            Stage("SetupPaused", Loc.Get("YourProgressIsSaved"), 0);
            throw;
        }
        catch (Exception error)
        {
            Log(error.ToString());
            Stage("CouldnTConnect", error.Message, 0, LogPath, Loc.Get("OpenSetupLog"));
            throw;
        }
        finally { _operation = null; }
    }

    internal static (string Target, string Serial)? ReadWireless(string root)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config/wireless-headset.json")))!;
            var target = node["adbTarget"]!.GetValue<string>();
            var serial = node["usbSerial"]!.GetValue<string>();
            return !string.IsNullOrWhiteSpace(serial) && Regex.IsMatch(target, @"^\d{1,3}(\.\d{1,3}){3}:\d{2,5}$") ? (target, serial) : null;
        }
        catch { return null; }
    }

    internal static string ModuleSource(string root) => Path.Combine(root, "vrcft-gaze-bridge/bin/Release/net10.0/Qpro.GazeBridge.dll");

    internal static void ValidateModuleFiles(string root, bool installModule)
    {
        if (installModule && (!File.Exists(ModuleSource(root)) || new FileInfo(ModuleSource(root)).Length == 0))
            throw new IOException(Loc.Get("TheQFTVRCFaceTrackingModuleIsMissingOrIncompleteRepairOr"));
    }

    internal static string EyeModel(string root) => Path.Combine(root, "models/eye/bolt-independent-axes.ptl");

    static readonly string[] ConflictingModules = ["7f9be083-a4f1-4e30-b28a-8e6ec878d583", "91a90618-b020-4064-8832-809b2ca2b3bc", "2a8c8080-2a76-46af-bf76-1da7c0127ef8"];

    internal async Task InstallModule(string source, string destination)
    {
        var folder = Path.GetDirectoryName(destination)!;
        var staged = destination + ".tmp";
        try
        {
            Directory.CreateDirectory(folder);
            File.Copy(source, staged, true);
            if (!SameFile(source, staged)) throw new IOException(Loc.Get("TheModuleCopyCouldNotBeVerifiedTryAgain"));
            for (var attempt = 0; ; attempt++)
                try { if (File.Exists(destination)) File.Replace(staged, destination, null); else File.Move(staged, destination); break; }
                catch (IOException error) when (attempt < 8 && (error.HResult & 0xFFFF) is 32 or 33) { await Task.Delay(250); }
            foreach (var id in ConflictingModules)
                if (Directory.Exists(Path.Combine(folder, id)))
                {
                    Directory.CreateDirectory(Path.Combine(root, "backups"));
                    Directory.Move(Path.Combine(folder, id), Path.Combine(root, "backups", $"vrcft-{id}-{Guid.NewGuid():N}"));
                }
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
        { throw new IOException(Loc.Get("TheModuleFileIsStillInUseQuitVRCFaceTrackingFrom"), error); }
        catch (UnauthorizedAccessException error)
        { throw new IOException(Loc.Get("WindowsBlockedTheModuleUpdateCheckAccessToTheVRCFaceTracking"), error); }
        finally { File.Delete(staged); }
    }

    private async Task PrepareEyeModelAsync(string target, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"qpro-stock-eye-{Guid.NewGuid():N}.ptl");
        try
        {
            (int Code, string Text) read;
            await using (var file = File.Create(temporary))
                read = await Processes.RunAsync(Processes.Info(adb, ["-s", target, "exec-out", "su -c " + Adb.Quote("cat " + Tracking.EyeTarget)]), token, 300, output: file);
            if (read.Code != 0 || new FileInfo(temporary).Length < 100_000) throw new IOException(Loc.Format("ReadingTheHeadsetEyeModelFailed", read.Text));
            Directory.CreateDirectory(Path.GetDirectoryName(EyeModel(root))!);
            if (await PythonRuntime.RunAsync(PythonRuntime.Exe(root), [Path.Combine(root, "eye_model_patch.py"), temporary, EyeModel(root)], token) != 0 || !File.Exists(EyeModel(root)))
                throw new IOException(Loc.Get("CreatingTheLocalIndependentEyePatchFailed"));
        }
        catch (IOException error) when (!token.IsCancellationRequested)
        {
            Log(error.ToString());
            throw new IOException(Loc.Get("CouldnTPrepareEyeTrackingForThisHeadsetMakeSure"), error);
        }
        finally { File.Delete(temporary); }
    }

    private async Task<(string Target, string Serial)> EnableWirelessAsync(string serial, CancellationToken token)
    {
        var problem = Loc.Get("CouldnTReachTheHeadsetOverWiFiKeepUSB");
        var route = await ProbeAsync(["-s", serial, "shell", "ip", "-4", "route", "get", "1.1.1.1"], token, 10);
        var address = Regex.Match(route.Text, @"\bsrc\s+(\d+\.\d+\.\d+\.\d+)");
        if (route.Code != 0 || !address.Success) throw new IOException(problem);
        var target = address.Groups[1].Value + ":5555";
        if ((await ProbeAsync(["-s", serial, "tcpip", "5555"], token, 10)).Code != 0) throw new IOException(problem);
        await Task.Delay(2000, token);
        await ProbeAsync(["connect", target], token, 10);
        var check = await ProbeAsync(["-s", target, "shell", "getprop", "ro.serialno"], token, 10);
        if (check.Code != 0 || check.Text.Trim() != serial) throw new IOException(problem);
        var su = await ProbeAsync(["-s", target, "shell", "su", "-c", "id"], token, 10, "uid=0(root)");
        if (!su.Text.Contains("uid=0(root)")) throw new IOException(Loc.Get("RootAccessIsUnavailableOverWiFiInMagiskSuperuser"));
        SaveWireless(root, target, serial);
        return (target, serial);
    }

    internal static string VirtualDesktopStreamer => Path.Combine(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Virtual Desktop, Inc.\Virtual Desktop Streamer", "Path", null) as string
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer"), "VirtualDesktop.Streamer.exe");

    private async Task WaitForVirtualDesktopAsync(CancellationToken token)
    {
        while (!File.Exists(VirtualDesktopStreamer) && !Processes.Running("VirtualDesktop.Streamer"))
        {
            Stage("InstallVirtualDesktop", Loc.Get("HandTrackingUsesVirtualDesktopInstallTheVirtualDesktopStreamer"), 60,
                "https://www.vrdesktop.net/", Loc.Get("GetVirtualDesktop"));
            await Task.Delay(2500, token);
        }
    }

    internal async Task<string?> HandSettingsProblemAsync(string target, CancellationToken token)
    {
        var read = await ProbeAsync(Adb.Su(target, "oculuspreferences --get hand_tracking_enabled; oculuspreferences --get multimodal_hands_and_controllers_enabled; oculuspreferences --getc simultaneous_hands_and_controllers_mode; pidof frida-server >/dev/null && echo SINGULARITY_FRIDA"), token, 10);
        return HandSettingsProblem(read.Text);
    }

    internal static string? HandSettingsProblem(string output)
    {
        var values = Regex.Matches(output, @"\[(\w+) : (\w+)\]").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        bool Off(string key) => values.TryGetValue(key, out var value) && value is "false" or "0";
        var problems = new List<string>();
        if (Off("hand_tracking_enabled")) problems.Add(Loc.Get("TurnOnHandTrackingInTheHeadsetSettingsMovementTracking"));
        if (Off("multimodal_hands_and_controllers_enabled") || Off("simultaneous_hands_and_controllers_mode")) problems.Add(Loc.Get("InSingularityTurnOnSimultaneousHandsControllers"));
        if (output.Contains("SINGULARITY_FRIDA")) problems.Add(Loc.Get("InSingularityTurnOffFridaServerHybridTrackingStartsIts"));
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private async Task WaitForHandSettingsAsync(string target, CancellationToken token)
    {
        while (await HandSettingsProblemAsync(target, token) is {} problem)
        {
            Stage("SetUpHandTracking", Loc.Format("SetupContinuesOnItsOwn", problem), 65);
            await Task.Delay(2500, token);
        }
    }

    internal static bool SameFile(string first, string second) => File.Exists(first) && File.Exists(second) && WorkingCopy.Hash(first) == WorkingCopy.Hash(second);

    internal async Task<(string Target, string Serial)> ConnectQuestAsync(CancellationToken token, string mode = "auto", string preferredSerial = "")
    {
        if (mode is not ("auto" or "usb" or "wifi")) throw new ArgumentException(Loc.Get("UnknownConnectionPreference"));
        var saved = ReadWireless(root);
        if (mode != "usb" && saved is {} wifi && (preferredSerial.Length == 0 || preferredSerial == wifi.Serial))
        {
            await ProbeAsync(["connect", wifi.Target], token);
            var serial = await ProbeAsync(["-s", wifi.Target, "shell", "getprop", "ro.serialno"], token);
            if (serial.Code == 0 && serial.Text.Trim() == wifi.Serial && await IsQuest(wifi.Target, token))
            {
                var su = await ProbeAsync(["-s", wifi.Target, "shell", "su", "-c", "id"], token, successPrefix: "uid=0(root)");
                if (su.Code == 0 && su.Text.Contains("uid=0(root)")) { SaveWireless(root, wifi.Target, wifi.Serial); return wifi; }
            }
        }
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var found = await DiscoverAsync(token);
            var candidates = found.Where(q => q.State == "device" && (preferredSerial.Length == 0 || q.Serial == preferredSerial) && (mode != "usb" || !q.Wireless)).ToArray();
            var serials = candidates.Select(q => q.Serial).Distinct().ToArray();
            if (serials.Length == 1)
            {
                var selected = candidates.OrderBy(q => q.Wireless ? 0 : 1).First();
                Stage("AllowTheConnection", Loc.Get("InMagiskSuperuserAllowShellAccess"), 15, "https://github.com/Lumince/singularity", Loc.Get("RootingInformation"));
                var su = await ProbeAsync(["-s", selected.Target, "shell", "su", "-c", "id"], token, 10, "uid=0(root)");
                if (su.Code == 0 && su.Text.Contains("uid=0(root)"))
                {
                    if (selected.Wireless) { SaveWireless(root, selected.Target, selected.Serial); return (selected.Target, selected.Serial); }
                    if (mode == "usb") return (selected.Target, selected.Serial);
                    Stage("ConnectingOverWiFi", Loc.Get("KeepUSBConnectedForThisStep"), 25);
                    try { return await EnableWirelessAsync(selected.Target, token); }
                    catch (IOException) when (mode == "auto")
                    {
                        var state = await ProbeAsync(["-s", selected.Target, "get-state"], token);
                        if (state.Code == 0 && state.Text.Trim() == "device") return (selected.Target, selected.Serial);
                        throw;
                    }
                }
                if (su.Text.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(Loc.Get("RootYourQuestProBeforeSetupOpenRootingInformationTo"));
            }
            else if (serials.Length > 1) throw new InvalidOperationException(Loc.Get("ChooseAHeadsetInSetup"));
            else if (found.Any(q => q.State == "unauthorized"))
                Stage("AllowTheConnection", Loc.Get("InTheHeadsetChooseAlwaysAllowFromThisComputer"), 10);
            else if (found.Any(q => q.State == "offline"))
                Stage("RestartYourHeadset", Loc.Get("YourQuestProIsConnectedButNotRespondingRestartIt"), 5);
            else Stage("ConnectYourQuestPro", Loc.Get("PlugInAUSBDataCableAndWakeTheHeadset"), 5);
            await Task.Delay(2500, token);
        }
    }

    internal sealed record Headset(string Target, string Serial, bool Wireless, string State);

    private async Task<bool> IsQuest(string target, CancellationToken token)
    {
        var model = await ProbeAsync(["-s", target, "shell", "getprop", "ro.product.device"], token);
        return model.Code == 0 && model.Text.Trim() == "seacliff";
    }

    internal async Task<List<Headset>> DiscoverAsync(CancellationToken token)
    {
        if(ReadWireless(root) is {} known) await ProbeAsync(["connect",known.Target],token);
        var mdns = await ProbeAsync(["mdns", "services"], token);
        foreach (Match match in Regex.Matches(mdns.Text, @"_adb(?:-tls-connect)?\._tcp\.?\s+(\d{1,3}(?:\.\d{1,3}){3}:\d{1,5})"))
            await ProbeAsync(["connect", match.Groups[1].Value], token);
        var devices = await ProbeAsync(["devices"], token);
        var result = new List<Headset>();
        foreach (Match match in Regex.Matches(devices.Text, @"(?m)^(\S+)\s+(device|unauthorized|offline)(?:\s|$)"))
        {
            var target = match.Groups[1].Value; var state = match.Groups[2].Value;
            if (target.StartsWith("emulator-")) continue;
            if (state is "unauthorized" or "offline") { result.Add(new(target, target, target.Contains(':'), state)); continue; }
            if (state != "device" || !await IsQuest(target, token)) continue;
            var serial = await ProbeAsync(["-s", target, "shell", "getprop", "ro.serialno"], token);
            if (serial.Code == 0 && serial.Text.Trim().Length > 0)
                result.Add(new(target, serial.Text.Trim(), target.Contains(':') || target.StartsWith("adb-"), state));
        }
        return result;
    }

    private static void SaveWireless(string root, string target, string serial)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config/wireless-headset.json"), JsonSerializer.Serialize(new { adbTarget = target, usbSerial = serial, transport = "adb-tcp" }));
    }

    internal static void SaveAutomaticSetup(string root, string target, string path)
    {
        var config = CalibrationSettings.ReadJson(path);
        config["root"] = root;
        config["adbTarget"] = target;
        CalibrationSettings.WriteJson(path, config);
    }

    private Task<(int Code, string Text)> ProbeAsync(string[] args, CancellationToken token, int seconds = 4, string? successPrefix = null) =>
        Processes.RunAsync(adb, args, token, seconds, successPrefix);

    internal static bool SteamAppInstalled(string id) => Registry.GetValue($@"HKEY_CURRENT_USER\Software\Valve\Steam\Apps\{id}", "Installed", 0) is 1;

    private async Task WaitForSteamAppAsync(string id, string name, CancellationToken token)
    {
        while (!SteamAppInstalled(id))
        {
            var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (steam is null || !File.Exists(Path.Combine(steam, "steam.exe")))
                Stage("InstallSteam", Loc.Get("InstallSteamAndSignInToContinue"), 60,
                    "https://store.steampowered.com/about/", Loc.Get("GetSteam"));
            else
                Stage(Loc.Format("Install", name), Loc.Format("InstallThroughSteamToContinue", name), 60,
                    $"steam://install/{id}", $"Install {name}");
            await Task.Delay(2500, token);
        }
    }

    private async Task CloseVrcftAsync(CancellationToken token)
    {
        var processes = Process.GetProcessesByName("VRCFaceTracking");
        try
        {
            foreach (var process in processes) try { process.CloseMainWindow(); } catch (InvalidOperationException) { }
            var exited = Task.WhenAll(processes.Select(process => process.WaitForExitAsync(token)));
            if (await Task.WhenAny(exited, Task.Delay(10000, token)) != exited)
                Stage("QuitVRCFaceTracking", Loc.Get("ChooseQuitFromItsSystemTrayMenuItWillReopen"), 70);
            await exited;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private void Log(string text) => Session.Log(LogPath, $"{DateTimeOffset.Now:O} {text}");
}

internal sealed class ModuleNotInstalledException(string message) : IOException(message);
