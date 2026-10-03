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
    internal string HelpCaption { get; private set; } = "Help";
    internal int StageProgress { get; private set; }

    private void Stage(string title, string text, int progress, string? help = null, string helpText = "Help")
    {
        if (_stage != title) Log(title);
        _stage = title;
        StageProgress = progress;
        HelpTarget = help;
        HelpCaption = helpText;
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
                if (!File.Exists(file)) throw new IOException("The app is missing a required file. Run the complete installer again.");
            Stage("Connecting", "Looking for your Quest Pro…", 5);
            await Adb.EnsureAsync(adb, token: token);
            var connection = await ConnectQuestAsync(token, connectionMode, preferredSerial);

            if (!PythonRuntime.Ready(root))
            {
                Stage("Preparing components", "One-time setup…", 35);
                try { await PythonRuntime.EnsureAsync(root, token); }
                catch (IOException error) when (!token.IsCancellationRequested)
                { Log(error.ToString()); throw new IOException("The PC components didn’t finish installing. Make sure this PC has free disk space, then try again.", error); }
            }

            var independentGaze = CalibrationSettings.ReadJson(AutoPath)["independentGaze"]?.GetValue<bool>() != false;
            if (!face) Stage("Skipping face tracking", "Face tracking isn’t selected.", 55);
            else if (independentGaze)
            {
                Stage("Preparing eye tracking", "Keep your headset awake.", 55);
                if (!File.Exists(EyeModel(root))) await PrepareEyeModelAsync(connection.Target, token);
            }
            else Stage("Using standard eye tracking", "Independent eye gaze is off.", 55);

            if (face) await WaitForSteamAppAsync("3329480", "VRCFaceTracking", token);
            await WaitForSteamAppAsync("250820", "SteamVR", token);
            if (use == "hands") { await WaitForVirtualDesktopAsync(token); await WaitForHandSettingsAsync(connection.Target, token); }
            if (face && installModule && !SameFile(source, installed))
            {
                Stage("Updating tracking", "Installing the updated VRCFaceTracking bridge…", 70);
                if (Processes.Running("VRCFaceTracking")) await CloseVrcftAsync(token);
                await InstallModule(source, installed);
                if (!SameFile(source, installed)) throw new IOException("The VRCFaceTracking module did not finish installing. Try again. Open the setup log for details.");
            }
            if (face && (!File.Exists(installed) || new FileInfo(installed).Length == 0))
            {
                Stage("Updating tracking", "", 70);
                throw new ModuleNotInstalledException(SteamVr.IsSteamLink
                    ? "Face tracking needs the QFT+ module in VRCFaceTracking, and automatic install is off. Install it to continue."
                    : "Face tracking needs the QFT+ module in VRCFaceTracking, and automatic install is off. Install it, or use hand tracking only.");
            }
            SaveAutomaticSetup(root, connection.Target, AutoPath);
            SteamVr.ConfigureSteamLink();
            Stage("Ready", "", 100);
        }
        catch (OperationCanceledException)
        {
            Stage("Setup paused", "Your progress is saved.", 0);
            throw;
        }
        catch (Exception error)
        {
            Log(error.ToString());
            Stage("Couldn’t connect", error.Message, 0, LogPath, "Open setup log");
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
            throw new IOException("The QFT+ VRCFaceTracking module is missing or incomplete. Repair or reinstall QFT+ before trying setup again.");
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
            if (!SameFile(source, staged)) throw new IOException("The module copy could not be verified. Try again.");
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
        { throw new IOException("The module file is still in use. Quit VRCFaceTracking from its system tray menu, then try again.", error); }
        catch (UnauthorizedAccessException error)
        { throw new IOException("Windows blocked the module update. Check access to the VRCFaceTracking CustomLibs folder and try again.", error); }
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
            if (read.Code != 0 || new FileInfo(temporary).Length < 100_000) throw new IOException("Reading the headset eye model failed: " + read.Text);
            Directory.CreateDirectory(Path.GetDirectoryName(EyeModel(root))!);
            if (await PythonRuntime.RunAsync(PythonRuntime.Exe(root), [Path.Combine(root, "eye_model_patch.py"), temporary, EyeModel(root)], token) != 0 || !File.Exists(EyeModel(root)))
                throw new IOException("Creating the local independent-eye patch failed.");
        }
        catch (IOException error) when (!token.IsCancellationRequested)
        {
            Log(error.ToString());
            throw new IOException("Couldn’t prepare eye tracking for this headset. Make sure it’s awake and Magisk allows Shell, then try again. The Setup log has the exact error.", error);
        }
        finally { File.Delete(temporary); }
    }

    private async Task<(string Target, string Serial)> EnableWirelessAsync(string serial, CancellationToken token)
    {
        const string problem = "Couldn’t reach the headset over Wi-Fi. Keep USB connected, put the PC and headset on the same home network (not guest Wi-Fi), then try again.";
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
        if (!su.Text.Contains("uid=0(root)")) throw new IOException("Root access is unavailable over Wi-Fi. In Magisk → Superuser, allow Shell, then try again.");
        SaveWireless(root, target, serial);
        return (target, serial);
    }

    internal static string VirtualDesktopStreamer => Path.Combine(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Virtual Desktop, Inc.\Virtual Desktop Streamer", "Path", null) as string
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer"), "VirtualDesktop.Streamer.exe");

    private async Task WaitForVirtualDesktopAsync(CancellationToken token)
    {
        while (!File.Exists(VirtualDesktopStreamer) && !Processes.Running("VirtualDesktop.Streamer"))
        {
            Stage("Install Virtual Desktop", "Hand tracking uses Virtual Desktop. Install the Virtual Desktop Streamer on this PC to continue.", 60,
                "https://www.vrdesktop.net/", "Get Virtual Desktop");
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
        if (Off("hand_tracking_enabled")) problems.Add("Turn on hand tracking in the headset: Settings → Movement tracking → Hand tracking.");
        if (Off("multimodal_hands_and_controllers_enabled") || Off("simultaneous_hands_and_controllers_mode")) problems.Add("In Singularity, turn on Simultaneous Hands & Controllers.");
        if (output.Contains("SINGULARITY_FRIDA")) problems.Add("In Singularity, turn off Frida Server. Hybrid tracking starts its own.");
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private async Task WaitForHandSettingsAsync(string target, CancellationToken token)
    {
        while (await HandSettingsProblemAsync(target, token) is {} problem)
        {
            Stage("Set up hand tracking", problem + " Setup continues on its own.", 65);
            await Task.Delay(2500, token);
        }
    }

    internal static bool SameFile(string first, string second) => File.Exists(first) && File.Exists(second) && WorkingCopy.Hash(first) == WorkingCopy.Hash(second);

    internal async Task<(string Target, string Serial)> ConnectQuestAsync(CancellationToken token, string mode = "auto", string preferredSerial = "")
    {
        if (mode is not ("auto" or "usb" or "wifi")) throw new ArgumentException("Unknown connection preference");
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
                Stage("Allow the connection", "In Magisk → Superuser, allow Shell access.", 15, "https://github.com/Lumince/singularity", "Rooting information");
                var su = await ProbeAsync(["-s", selected.Target, "shell", "su", "-c", "id"], token, 10, "uid=0(root)");
                if (su.Code == 0 && su.Text.Contains("uid=0(root)"))
                {
                    if (selected.Wireless) { SaveWireless(root, selected.Target, selected.Serial); return (selected.Target, selected.Serial); }
                    if (mode == "usb") return (selected.Target, selected.Serial);
                    Stage("Connecting over Wi-Fi", "Keep USB connected for this step.", 25);
                    try { return await EnableWirelessAsync(selected.Target, token); }
                    catch (IOException) when (mode == "auto")
                    {
                        var state = await ProbeAsync(["-s", selected.Target, "get-state"], token);
                        if (state.Code == 0 && state.Text.Trim() == "device") return (selected.Target, selected.Serial);
                        throw;
                    }
                }
                if (su.Text.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Root your Quest Pro before setup. Open Rooting information to check compatibility.");
            }
            else if (serials.Length > 1) throw new InvalidOperationException("Choose a headset in Setup.");
            else if (found.Any(q => q.State == "unauthorized"))
                Stage("Allow the connection", "In the headset, choose “Always allow from this computer,” then Allow.", 10);
            else if (found.Any(q => q.State == "offline"))
                Stage("Restart your headset", "Your Quest Pro is connected but not responding. Restart it and keep USB connected. Setup continues on its own.", 5);
            else Stage("Connect your Quest Pro", "Plug in a USB data cable and wake the headset. Developer Mode must be enabled.", 5);
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
                Stage("Install Steam", "Install Steam and sign in to continue.", 60,
                    "https://store.steampowered.com/about/", "Get Steam");
            else
                Stage($"Install {name}", $"Install {name} through Steam to continue.", 60,
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
                Stage("Quit VRCFaceTracking", "Choose Quit from its system tray menu. It will reopen automatically.", 70);
            await exited;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private void Log(string text) => Session.Log(LogPath, $"{DateTimeOffset.Now:O} {text}");
}

internal sealed class ModuleNotInstalledException(string message) : IOException(message);
