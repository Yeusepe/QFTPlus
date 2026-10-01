using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace QproFaceTracking.Hub;

internal sealed class SetupService(string root) : IDisposable
{
    private readonly string _root = root;
    private CancellationTokenSource? _operation;
    private string? _stage;
    internal static string AutoPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "QproAutoStart.json");
    private string Adb => Environment.GetEnvironmentVariable("QPRO_ADB") ?? Path.Combine(_root, "platform-tools", "adb.exe");
    private string LogPath => Path.Combine(_root, "setup.log");
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
            var source = ModuleSource(_root);
            var installed = Path.Combine(Path.GetDirectoryName(AutoPath)!, "CustomLibs/000-Qpro.IndependentGaze.dll");
            if (face) ValidateModuleFiles(_root, installModule);
            foreach (var file in new[] { "platform-tools/adb.exe", "setup-runtime.ps1", "enable-quest-wireless.ps1", "autostart_runtime.py" })
                if (!File.Exists(Path.Combine(_root, file))) throw new IOException("The app is missing a required file. Run the complete installer again.");
            Stage("Connecting", "Looking for your Quest Pro…", 5);
            await AdbServer.EnsureAsync(Adb, token: token);
            var connection = await ConnectQuestAsync(token, connectionMode, preferredSerial);

            if (!File.Exists(Path.Combine(_root, "runtime/runtime-ready.json")) || !File.Exists(FindPython(_root)))
            {
                Stage("Preparing components", "One-time setup…", 35);
                await ScriptAsync("setup-runtime.ps1", [], token);
            }

            var independentGaze = !File.Exists(AutoPath) || CalibrationSettings.ReadJson(AutoPath)["independentGaze"]?.GetValue<bool>() != false;
            if (!face) Stage("Skipping face tracking", "Face tracking isn’t selected.", 55);
            else if (independentGaze)
            {
                Stage("Preparing eye tracking", "Keep your headset awake.", 55);
                if (File.Exists(AutoPath))
                {
                    var oldRoot = CalibrationSettings.ReadJson(AutoPath)["root"]?.GetValue<string>();
                    var patch = "models/eye/bolt-independent-axes.ptl";
                    if (oldRoot is not null && File.Exists(Path.Combine(oldRoot, patch)) && !File.Exists(Path.Combine(_root, patch)))
                    { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, patch))!); File.Copy(Path.Combine(oldRoot, patch), Path.Combine(_root, patch)); }
                }
                if (!File.Exists(Path.Combine(_root, "models/eye/bolt-independent-axes.ptl")))
                    await ScriptAsync("prepare-eye-model.ps1", [], token, connection.Target);
            }
            else Stage("Using standard eye tracking", "Independent eye gaze is off.", 55);

            if (face) await WaitForSteamAppAsync("3329480", "VRCFaceTracking", token);
            await WaitForSteamAppAsync("250820", "SteamVR", token);
            if (use == "hands") { await WaitForVirtualDesktopAsync(token); await WaitForHandSettingsAsync(connection.Target, token); }
            if (face && installModule && !SameFile(source, installed))
            {
                Stage("Updating tracking", "Installing the updated VRCFaceTracking bridge…", 70);
                if (IsRunning("VRCFaceTracking")) await CloseVrcftAsync(token);
                await StopHelpersAsync(token);
                await ScriptAsync("install-vrcft-eye-bridge.ps1", [], token);
                if (!SameFile(source, installed)) throw new IOException("The VRCFaceTracking module did not finish installing. Try again. Open the setup log for details.");
            }
            if (face && (!File.Exists(installed) || new FileInfo(installed).Length == 0))
            {
                Stage("Updating tracking", "", 70);
                throw new ModuleNotInstalledException(SteamVr.IsSteamLink
                    ? "Face tracking needs the QFT+ module in VRCFaceTracking, and automatic install is off. Install it to continue."
                    : "Face tracking needs the QFT+ module in VRCFaceTracking, and automatic install is off. Install it, or use hand tracking only.");
            }
            SaveAutomaticSetup(_root, connection.Target, AutoPath);
            File.Delete(Path.Combine(_root, ".qpro-manual.stop"));
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
        if (installModule && (!File.Exists(ModuleSource(root)) || new FileInfo(ModuleSource(root)).Length == 0 || !File.Exists(Path.Combine(root, "install-vrcft-eye-bridge.ps1"))))
            throw new IOException("The QFT+ VRCFaceTracking module is missing or incomplete. Repair or reinstall QFT+ before trying setup again.");
    }

    internal static string VirtualDesktopStreamer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer/VirtualDesktop.Streamer.exe");

    private async Task WaitForVirtualDesktopAsync(CancellationToken token)
    {
        while (!File.Exists(VirtualDesktopStreamer))
        {
            Stage("Install Virtual Desktop", "Hand tracking uses Virtual Desktop. Install the Virtual Desktop Streamer on this PC to continue.", 60,
                "https://www.vrdesktop.net/", "Get Virtual Desktop");
            await Task.Delay(2500, token);
        }
    }

    internal async Task<string?> HandSettingsProblemAsync(string target, CancellationToken token)
    {
        var read = await ProbeAsync(["-s", target, "shell", "su -c 'oculuspreferences --get hand_tracking_enabled; oculuspreferences --get multimodal_hands_and_controllers_enabled; oculuspreferences --getc simultaneous_hands_and_controllers_mode; pidof frida-server >/dev/null && echo SINGULARITY_FRIDA'"], token, 10);
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

    internal static bool SameFile(string first, string second)
    {
        if (!File.Exists(first) || !File.Exists(second)) return false;
        using var a = File.OpenRead(first); using var b = File.OpenRead(second);
        return System.Security.Cryptography.SHA256.HashData(a).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
    }

    internal async Task<(string Target, string Serial)> ConnectQuestAsync(CancellationToken token, string mode = "auto", string preferredSerial = "")
    {
        if (mode is not ("auto" or "usb" or "wifi")) throw new ArgumentException("Unknown connection preference");
        var saved = ReadWireless(_root);
        if (saved is null && File.Exists(AutoPath))
        {
            var previous = CalibrationSettings.ReadJson(AutoPath)["root"]?.GetValue<string>();
            if (previous is not null) saved = ReadWireless(previous);
        }
        if (mode != "usb" && saved is {} wifi && (preferredSerial.Length == 0 || preferredSerial == wifi.Serial))
        {
            await ProbeAsync(["connect", wifi.Target], token);
            var serial = await ProbeAsync(["-s", wifi.Target, "shell", "getprop", "ro.serialno"], token);
            if (serial.Code == 0 && serial.Text.Trim() == wifi.Serial && await IsQuest(wifi.Target, token))
            {
                var root = await ProbeAsync(["-s", wifi.Target, "shell", "su", "-c", "id"], token, successPrefix: "uid=0(root)");
                if (root.Code == 0 && root.Text.Contains("uid=0(root)")) { SaveWireless(_root, wifi.Target, wifi.Serial); return wifi; }
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
                var root = await ProbeAsync(["-s", selected.Target, "shell", "su", "-c", "id"], token, 10, "uid=0(root)");
                if (root.Code == 0 && root.Text.Contains("uid=0(root)"))
                {
                    if (selected.Wireless) { SaveWireless(_root, selected.Target, selected.Serial); return (selected.Target, selected.Serial); }
                    if (mode == "usb") return (selected.Target, selected.Serial);
                    Stage("Connecting over Wi-Fi", "Keep USB connected for this step.", 25);
                    try
                    {
                        await ScriptAsync("enable-quest-wireless.ps1", ["-UsbSerial", selected.Target], token);
                        return ReadWireless(_root) ?? throw new IOException("Wi-Fi connection was not saved.");
                    }
                    catch (IOException) when (mode == "auto")
                    {
                        var state = await ProbeAsync(["-s", selected.Target, "get-state"], token);
                        if (state.Code == 0 && state.Text.Trim() == "device") return (selected.Target, selected.Serial);
                        throw;
                    }
                }
                if (root.Text.Contains("not found", StringComparison.OrdinalIgnoreCase))
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

    internal sealed record Headset(string Target, string Serial, bool Wireless, string State)
    { public override string ToString() => $"Quest Pro · {(Wireless ? "Wi-Fi" : "USB")} · {Serial}"; }

    private async Task<bool> IsQuest(string target, CancellationToken token)
    {
        var model = await ProbeAsync(["-s", target, "shell", "getprop", "ro.product.device"], token);
        return model.Code == 0 && model.Text.Trim() == "seacliff";
    }

    internal async Task<List<Headset>> DiscoverAsync(CancellationToken token)
    {
        var saved=ReadWireless(_root);
        if(saved is null && File.Exists(AutoPath) && CalibrationSettings.ReadJson(AutoPath)["root"]?.GetValue<string>() is {} previous) saved=ReadWireless(previous);
        if(saved is {} known) await ProbeAsync(["connect",known.Target],token);
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
        var config = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        var previousRoot = config["root"]?.GetValue<string>();
        var configuredPython = config["runtimePython"]?.GetValue<string>();
        var keepExternalRuntime = configuredPython is not null && File.Exists(configuredPython)
            && previousRoot is not null && !Path.GetFullPath(configuredPython).StartsWith(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(previousRoot)) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        if (previousRoot is not null && !Path.GetFullPath(previousRoot).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
        {
            var previous = Path.Combine(previousRoot, "calibration/qpro-pupil-dilation.json");
            var next = Path.Combine(root, "calibration/qpro-pupil-dilation.json");
            if (File.Exists(previous) && !File.Exists(next)) { Directory.CreateDirectory(Path.GetDirectoryName(next)!); File.Copy(previous, next); }
            var oldSettings=Path.Combine(previousRoot,"output-settings.json");
            var newSettings=Path.Combine(root,"output-settings.json");
            if(File.Exists(oldSettings)&&!File.Exists(newSettings))File.Copy(oldSettings,newSettings);
            foreach(var key in new[]{"tongueModelPath","tongueDirectionModelPath"}.Concat(CalibrationSettings.FaceGroups.Select(group=>CalibrationSettings.FaceModelKey(group.Kind))))
            {
                if(config[key]?.GetValue<string>() is not {} source || !File.Exists(source))continue;
                using var file=File.OpenRead(source);
                var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file))[..12];
                var destination=Path.Combine(root,"models",$"personal-{key}-{hash}.pt");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if(!File.Exists(destination))File.Copy(source,destination);
                config[key]=destination;
            }
        }
        config["root"] = root;
        config["runtimePython"] = keepExternalRuntime ? configuredPython : FindPython(root);
        config["adbTarget"] = target;
        config["showPreviews"] ??= false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private async Task ScriptAsync(string script, string[] arguments, CancellationToken token, string? target = null)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(_root, script) }.Concat(arguments)) info.ArgumentList.Add(arg);
        info.Environment.Remove("PSModulePath");
        info.Environment["QPRO_ADB"] = Adb;
        if (target is not null) info.Environment["ANDROID_SERIAL"] = target;
        var python = FindPython(_root);
        if (File.Exists(python)) info.Environment["QPRO_PYTHON"] = python;
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("Windows could not start setup. Try opening the app again.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var details = (await output) + Environment.NewLine + (await errors);
        Log(script + Environment.NewLine + details);
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new IOException(ScriptFailure(script, details));
    }

    internal static string ScriptFailure(string script, string details)
    {
        var failure = Regex.Match(details, @"(?m)^QFT_SETUP_ERROR: (.+)$");
        if (failure.Success) return failure.Groups[1].Value.Trim();
        return script switch
            {
                "enable-quest-wireless.ps1" => "Couldn’t reach the headset over Wi-Fi. Keep USB connected, put the PC and headset on the same home network (not guest Wi-Fi), then try again.",
                "setup-runtime.ps1" => "The PC components didn’t finish installing. Check the internet connection and free disk space, then try again. Finished downloads are reused.",
                "prepare-eye-model.ps1" => "Couldn’t prepare eye tracking for this headset. Make sure it’s awake and Magisk allows Shell, then try again. The Setup log has the exact error.",
                _ => "Couldn’t update VRCFaceTracking. The Setup log has the exact error."
            };
    }

    internal static string FindPython(string root)
    {
        return Path.Combine(root, "runtime/python.exe");
    }

    private async Task<(int Code, string Text)> ProbeAsync(string[] args, CancellationToken token, int seconds = 4, string? successPrefix = null)
    {
        var info = new ProcessStartInfo(Adb) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        var output = ReadOutputAsync();
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            var text = await output;
            if (successPrefix is not null && text.StartsWith(successPrefix, StringComparison.Ordinal)) return (0, text);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, text + (await error));
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return (-1, "Connection timed out");
        }
        finally { try { process.Kill(); } catch (InvalidOperationException) { } }

        async Task<string> ReadOutputAsync()
        {
            if (successPrefix is null) return await process.StandardOutput.ReadToEndAsync(timeout.Token);
            var text = new StringBuilder();
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is {} line)
            {
                if (line.StartsWith(successPrefix, StringComparison.Ordinal)) return line;
                text.AppendLine(line);
            }
            return text.ToString();
        }
    }

    internal static string? SteamApp(string id)
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        steam ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps/libraryfolders.vdf");
        if (File.Exists(vdf)) libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{id}.acf");
            if (!File.Exists(manifest)) continue;
            var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
            if (match.Success)
            {
                var path = Path.GetFullPath(Path.Combine(library, "steamapps/common", match.Groups[1].Value));
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, id == "3329480" ? "VRCFaceTracking.exe" : "bin/win64/vrserver.exe"))) return path;
            }
        }
        return null;
    }

    private async Task WaitForSteamAppAsync(string id, string name, CancellationToken token)
    {
        while (SteamApp(id) is null)
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

    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        var running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return running;
    }

    private async Task CloseVrcftAsync(CancellationToken token)
    {
        foreach (var process in Process.GetProcessesByName("VRCFaceTracking"))
            using (process) { try { process.CloseMainWindow(); } catch (InvalidOperationException) { } }
        var attempts = 0;
        while (IsRunning("VRCFaceTracking"))
        {
            if (++attempts > 10) Stage("Quit VRCFaceTracking", "Choose Quit from its system tray menu. It will reopen automatically.", 70);
            await Task.Delay(1000, token);
        }
    }

    private async Task StopHelpersAsync(CancellationToken token)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _root };
        if (File.Exists(AutoPath))
        {
            var previous = JsonNode.Parse(File.ReadAllText(AutoPath))?["root"]?.GetValue<string>();
            if (previous is not null && Directory.Exists(previous)) roots.Add(previous);
        }
        foreach (var root in roots)
        {
            File.WriteAllText(Path.Combine(root, ".qpro-manual.stop"), "stop");
            foreach (var stop in Directory.GetFiles(root, ".qpro-autostart-*.stop")) File.WriteAllText(stop, "stop");
            var status = Path.Combine(root, "autostart-status.json");
            if (File.Exists(status))
            {
                var stop = CalibrationSettings.ReadJson(status)?["stopFile"]?.GetValue<string>();
                if (stop is not null && Path.GetDirectoryName(Path.GetFullPath(stop))!.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) File.WriteAllText(stop, "stop");
            }
            var lockPath = Path.Combine(root, ".qpro-autostart.lock");
            while (File.Exists(lockPath))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var stream = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                    stream.Lock(0, 1); stream.Unlock(0, 1);
                    break;
                }
                catch (IOException)
                {
                    if (!File.Exists(status)) await CloseVrcftAsync(token);
                    await Task.Delay(500, token);
                }
            }
        }
    }

    private void Log(string text) => File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
    public void Dispose() => CancelOperation();
}

internal sealed class ModuleNotInstalledException(string message) : IOException(message);
