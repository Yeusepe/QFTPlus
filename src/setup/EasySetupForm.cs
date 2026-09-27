using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace QproFaceTracking.Hub;

internal sealed class EasySetupForm : Form
{
    private readonly string _root;
    private readonly Label _heading = new() { AutoSize = true, Font = new Font("Segoe UI", 17, FontStyle.Bold), Margin = new(0, 0, 0, 8) };
    private readonly Label _message = new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new(0, 0, 0, 16) };
    private readonly Button _go = MakeButton("Start");
    private readonly Button _cancel = MakeButton("Cancel");
    private readonly Button _help = MakeButton("Help");
    private readonly ToolStripMenuItem _previews = new("Show camera previews") { CheckOnClick = true, ToolTipText = "Applies the next time tracking starts." };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 8, AccessibleName = "Setup progress", MarqueeAnimationSpeed = SystemInformation.UIEffectsEnabled ? 30 : 0, Visible = false, Margin = new(0, 0, 0, 16) };
    private readonly Dictionary<string, (Label Status, Button Button)> _calibrations = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private CancellationTokenSource? _operation;
    private string? _helpUrl;
    private bool _started;
    internal static string AutoPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCFaceTracking", "QproAutoStart.json");
    private string Adb => Environment.GetEnvironmentVariable("QPRO_ADB") ?? Path.Combine(_root, "platform-tools", "adb.exe");
    private string LogPath => Path.Combine(_root, "setup.log");

    public EasySetupForm(string root, bool startImmediately = false, string? calibrate = null)
    {
        _root = root;
        Text = "Quest Pro Face Tracking";
        MinimumSize = new(550, 460);
        ClientSize = new(576, 430);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new("Segoe UI", 11);
        var menu = new MenuStrip();
        var settings = new ToolStripMenuItem("&Settings");
        settings.DropDownItems.Add(_previews);
        var helpMenu = new ToolStripMenuItem("&Help");
        helpMenu.DropDownItems.Add("View logs", null, (_, _) => ShowDetails());
        helpMenu.DropDownItems.Add("Setup guide", null, (_, _) => Open(Path.Combine(_root, "README.md")));
        menu.Items.AddRange([settings, helpMenu]);
        MainMenuStrip = menu;
        FontChanged += (_, _) => _heading.Font = new(Font.FontFamily, Font.Size * 1.55f, FontStyle.Bold);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var page = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new(28, 24, 28, 24) };
        page.ColumnStyles.Add(new(SizeType.Percent, 100));
        page.Controls.Add(_heading);
        page.Controls.Add(_message);
        page.Controls.Add(_progress);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new(0, 0, 0, 24) };
        actions.Controls.AddRange([_go, _cancel, _help]);
        page.Controls.Add(actions);
        var group = new GroupBox { Text = "Calibration", AutoSize = true, Dock = DockStyle.Top, Padding = new(16, 24, 16, 12), Margin = new(0) };
        var rows = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3 };
        rows.ColumnStyles.Add(new(SizeType.Percent, 40));
        rows.ColumnStyles.Add(new(SizeType.Percent, 60));
        rows.ColumnStyles.Add(new(SizeType.AutoSize));
        foreach (var (kind, title) in new[] { ("tongue", "Tongue"), ("puff", "Cheek puff"), ("pupils", "Pupils") })
        {
            var name = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 8, 8, 8) };
            var status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new(0, 8, 8, 8) };
            var button = MakeButton("Calibrate…");
            button.Dock = DockStyle.Fill;
            button.AccessibleName = "Calibrate " + title.ToLowerInvariant();
            button.Click += async (_, _) => await CalibrateAsync(kind);
            rows.Controls.Add(name); rows.Controls.Add(status); rows.Controls.Add(button);
            _calibrations.Add(kind, (status, button));
        }
        group.Controls.Add(rows);
        page.Controls.Add(group);
        scroll.Controls.Add(page);
        Controls.Add(scroll);
        Controls.Add(menu);
        _help.Visible = _cancel.Visible = false;
        AcceptButton = _go;
        _go.Click += async (_, _) => { if (_started) await StopAsync(); else await SetupAsync(); };
        _cancel.Click += (_, _) => { _operation?.Cancel(); _cancel.Enabled = false; _message.Text = "Stopping…"; };
        _help.Click += (_, _) => { if (_helpUrl is not null) Open(_helpUrl); };
        _previews.Click += (_, _) =>
        {
            try
            {
                var config = File.Exists(AutoPath) ? JsonNode.Parse(File.ReadAllText(AutoPath))!.AsObject() : new JsonObject();
                config["showPreviews"] = _previews.Checked;
                Directory.CreateDirectory(Path.GetDirectoryName(AutoPath)!);
                File.WriteAllText(AutoPath + ".tmp", config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(AutoPath + ".tmp", AutoPath, true);
            }
            catch (Exception error) { _previews.Checked = !_previews.Checked; _message.Text = error.Message; }
        };
        FormClosing += (_, e) =>
        {
            if (_operation is null) return;
            e.Cancel = true;
            _operation.Cancel();
            _message.Text = "Stopping… Keep the headset connected.";
        };
        _timer.Tick += (_, _) => UpdateLiveStatus();
        _timer.Start();
        ShowWelcome();
        if (calibrate is not null) Shown += async (_, _) => await CalibrateAsync(calibrate);
        else if (startImmediately) Shown += async (_, _) => await SetupAsync();
    }

    private void ShowWelcome()
    {
        _heading.Text = "Face tracking";
        _message.Text = "Not connected";
        RefreshCalibrations();
    }

    private void RefreshCalibrations()
    {
        JsonObject config;
        try { config = JsonNode.Parse(File.ReadAllText(AutoPath))!.AsObject(); }
        catch { config = new(); }
        _previews.Checked = config["showPreviews"]?.GetValue<bool>() == true;
        var tongue = File.Exists(config["tongueModelPath"]?.GetValue<string>()) && File.Exists(config["tongueDirectionModelPath"]?.GetValue<string>());
        var puff = File.Exists(config["extraFaceModel"]?.GetValue<string>());
        var pupils = File.Exists(Path.Combine(_root, "calibration/qpro-pupil-dilation.json"));
        foreach (var (kind, calibrated) in new[] { ("tongue", tongue), ("puff", puff), ("pupils", pupils) })
        {
            var row = _calibrations[kind];
            row.Status.Text = calibrated ? "Calibrated" : kind == "tongue" ? "Default model" : "Not calibrated";
            row.Button.Text = calibrated ? "Recalibrate…" : "Calibrate…";
        }
    }

    private async Task CalibrateAsync(string kind)
    {
        if (_operation is not null) return;
        if (!_started) await SetupAsync();
        if (!_started) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        Busy(true);
        try
        {
            Stage("", "Preparing calibration", "Keep your headset connected.", 0);
            await StopHelpersAsync(operation.Token);
            using var calibration = new CalibrationForm(_root, kind);
            calibration.ShowDialog(this);
            RefreshCalibrations();
        }
        catch (Exception error) { Log(error.ToString()); _message.Text = error.Message; }
        finally
        {
            try
            {
                await CloseVrcftAsync(CancellationToken.None);
                File.Delete(Path.Combine(_root, ".qpro-manual.stop"));
                Open("steam://rungameid/3329480");
                Stage("", "Starting tracking", "Loading your calibration…", 0);
            }
            catch (Exception error) { Log(error.ToString()); _message.Text = error.Message; }
            _operation = null;
            _started = false;
            _go.Text = "Start";
            Busy(false);
        }
    }

    internal event Action<string, string>? StageChanged;
    internal void CancelOperation() => _operation?.Cancel();
    internal string? HelpTarget => _helpUrl;
    internal string HelpCaption => _help.Text;
    internal int StageProgress => _progress.Value;

    private void Stage(string step, string title, string text, int progress, string? help = null, string helpText = "Help")
    {
        if (_heading.Text != title) Log(title);
        _heading.Text = title;
        _message.Text = text;
        _progress.Value = progress;
        _helpUrl = help;
        _help.Text = helpText;
        _help.Visible = help is not null;
        StageChanged?.Invoke(title, text);
    }

    private void Busy(bool busy)
    {
        _go.Visible = !busy;
        _previews.Enabled = !busy;
        foreach (var row in _calibrations.Values) row.Button.Enabled = !busy;
        _cancel.Visible = busy;
        _cancel.Enabled = true;
        _progress.Visible = busy;
        _progress.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
    }

    internal async Task SetupAsync(bool prepareOnly = false, string connectionMode = "auto", string preferredSerial = "", bool installModule = true)
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        var token = operation.Token;
        Busy(true);
        _started = false;
        try
        {
            foreach (var file in new[] { "platform-tools/adb.exe", "setup-runtime.ps1", "enable-quest-wireless.ps1", "autostart_runtime.py" })
                if (!File.Exists(Path.Combine(_root, file))) throw new IOException("The app is missing a required file. Run the complete installer again.");
            Stage("GETTING READY", "Connecting", "Looking for your Quest Pro…", 5);
            var connection = await ConnectQuestAsync(token, connectionMode, preferredSerial);

            if (!File.Exists(Path.Combine(_root, "runtime/runtime-ready.json")) || !File.Exists(FindPython(_root)))
            {
                Stage("", "Preparing components", "One-time setup…", 35);
                await ScriptAsync("setup-runtime.ps1", [], token);
            }

            Stage("TRACKING SETUP", "Preparing eye tracking", "Keep your headset awake.", 55);
            if (File.Exists(AutoPath))
            {
                var oldRoot = CalibrationForm.ReadJson(AutoPath)["root"]?.GetValue<string>();
                var patch = "models/eye/bolt-independent-axes.ptl";
                if (oldRoot is not null && File.Exists(Path.Combine(oldRoot, patch)) && !File.Exists(Path.Combine(_root, patch)))
                { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_root, patch))!); File.Copy(Path.Combine(oldRoot, patch), Path.Combine(_root, patch)); }
            }
            if (!File.Exists(Path.Combine(_root, "models/eye/bolt-independent-axes.ptl")))
                await ScriptAsync("prepare-eye-model.ps1", [], token, connection.Target);

            await WaitForSteamAppAsync("3329480", "VRCFaceTracking", token);
            await WaitForSteamAppAsync("250820", "SteamVR", token);
            var source = Path.Combine(_root, "vrcft-gaze-bridge/bin/Release/net10.0/Qpro.GazeBridge.dll");
            var installed = Path.Combine(Path.GetDirectoryName(AutoPath)!, "CustomLibs/000-Qpro.IndependentGaze.dll");
            if (installModule && !SameFile(source, installed))
            {
                Stage("", "Updating tracking", "Installing the updated VRCFaceTracking bridge…", 70);
                if (IsRunning("VRCFaceTracking")) await CloseVrcftAsync(token);
                await StopHelpersAsync(token);
                await ScriptAsync("install-vrcft-eye-bridge.ps1", [], token);
            }
            if (!File.Exists(installed)) throw new IOException("Open Setup to install the VRCFaceTracking module.");
            SaveAutomaticSetup(_root, connection.Target, AutoPath);
            File.Delete(Path.Combine(_root, ".qpro-manual.stop"));
            if (prepareOnly) { Stage("", "Ready", "", 100); _started = true; return; }
            if (!IsRunning("vrserver")) Open("steam://rungameid/250820");
            var vd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Virtual Desktop Streamer", "VirtualDesktop.Streamer.exe");
            if (!IsRunning("VirtualDesktop.Streamer") && File.Exists(vd)) Open(vd);
            while (!IsRunning("vrserver"))
            {
                Stage("OPENING YOUR VR APPS", "Waiting for SteamVR", "Connect to this PC in Virtual Desktop.", 80);
                await Task.Delay(2000, token);
            }
            Open("steam://rungameid/3329480");
            while (!IsRunning("VRCFaceTracking") || !FaceDataReady())
            {
                Stage("ONE HEADSET SETTING", "Enable face and eye tracking", "In Virtual Desktop → Streaming, enable “Forward face/eye tracking to PC,” then enter SteamVR.", 90,
                    "https://www.vrdesktop.net/", "Virtual Desktop help");
                await Task.Delay(2000, token);
            }
            Stage("STARTING TRACKING", "Starting tracking", "Loading your models…", 95);
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (RuntimeState() != "running")
            {
                if (DateTime.UtcNow >= deadline) throw new IOException("Enhanced tracking didn’t start. Try again, or open Help → View logs.");
                await Task.Delay(1000, token);
            }
            _started = true;
            Stage("READY", "Tracking", "Connected over Wi-Fi. You can unplug USB and close this window.", 100);
            _go.Text = "Stop";
        }
        catch (OperationCanceledException)
        {
            Stage("SETUP PAUSED", "Setup paused", "Your progress is saved.", 0);
            _go.Text = "Continue";
            if (prepareOnly) throw;
        }
        catch (Exception error)
        {
            Log(error.ToString());
            Stage("LET’S FIX THIS", "Couldn’t connect", error.Message, 0, _helpUrl, _help.Text);
            _go.Text = "Try again";
            if (prepareOnly) throw;
        }
        finally { _operation = null; Busy(false); }
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

    internal static bool SameFile(string first, string second)
    {
        if (!File.Exists(first) || !File.Exists(second)) return false;
        using var a = File.OpenRead(first); using var b = File.OpenRead(second);
        return System.Security.Cryptography.SHA256.HashData(a).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
    }

    internal static string[] UsbDevices(string output, string state) => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => Regex.Match(line, @"^(\S+)\s+(device|unauthorized|offline)(?:\s|$)"))
        .Where(match => match.Success && !match.Groups[1].Value.Contains(':') && !match.Groups[1].Value.StartsWith("emulator-") && match.Groups[2].Value == state)
        .Select(match => match.Groups[1].Value).ToArray();

    internal async Task<(string Target, string Serial)> ConnectQuestAsync(CancellationToken token, string mode = "auto", string preferredSerial = "")
    {
        if (mode is not ("auto" or "usb" or "wifi")) throw new ArgumentException("Unknown connection preference");
        var saved = ReadWireless(_root);
        if (saved is null && File.Exists(AutoPath))
        {
            var previous = CalibrationForm.ReadJson(AutoPath)["root"]?.GetValue<string>();
            if (previous is not null) saved = ReadWireless(previous);
        }
        if (mode != "usb" && saved is {} wifi && (preferredSerial.Length == 0 || preferredSerial == wifi.Serial))
        {
            await ProbeAsync(["connect", wifi.Target], token);
            var serial = await ProbeAsync(["-s", wifi.Target, "shell", "getprop", "ro.serialno"], token);
            if (serial.Code == 0 && serial.Text.Trim() == wifi.Serial && await IsQuest(wifi.Target, token))
            {
                var root = await ProbeAsync(["-s", wifi.Target, "shell", "su", "-c", "id"], token);
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
                Stage("", "Allow the connection", "In Magisk → Superuser, allow Shell access.", 15, "https://github.com/Lumince/singularity", "Rooting information");
                var root = await ProbeAsync(["-s", selected.Target, "shell", "su", "-c", "id"], token, 10);
                if (root.Code == 0 && root.Text.Contains("uid=0(root)"))
                {
                    if (selected.Wireless) { SaveWireless(_root, selected.Target, selected.Serial); return (selected.Target, selected.Serial); }
                    if (mode == "usb") return (selected.Target, selected.Serial);
                    Stage("", "Connecting over Wi-Fi", "Keep USB connected for this step.", 25);
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
                Stage("", "Allow the connection", "In the headset, choose “Always allow from this computer,” then Allow.", 10);
            else if (found.Any(q => q.State == "offline"))
                Stage("", "Restart your headset", "Your Quest Pro is connected but not responding. Restart it and keep USB connected. Setup continues on its own.", 5);
            else Stage("", "Connect your Quest Pro", "Plug in a USB data cable and wake the headset. Developer Mode must be enabled.", 5);
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
        if(saved is null && File.Exists(AutoPath) && CalibrationForm.ReadJson(AutoPath)["root"]?.GetValue<string>() is {} previous) saved=ReadWireless(previous);
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
            foreach(var key in new[]{"tongueModelPath","tongueDirectionModelPath","extraFaceModel"})
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
            throw new IOException(script switch
            {
                "enable-quest-wireless.ps1" => "We couldn’t reach the headset over Wi-Fi. Keep USB connected, put the PC and headset on the same home network (not guest Wi-Fi), and click Try again.",
                "setup-runtime.ps1" => "The PC components didn’t finish installing. Check your internet connection and available disk space, then click Try again. Completed downloads can be reused.",
                "prepare-eye-model.ps1" => "We couldn’t prepare eye tracking for this headset. Check that it’s awake and Magisk allows Shell. If retrying fails, Help → View logs contains the firmware/patch error.",
                _ => "VRCFaceTracking could not be updated. Quit it from its system tray icon, then click Try again. Help → View logs contains the exact error."
            });
    }

    internal static string FindPython(string root)
    {
        return Path.Combine(root, "runtime/python.exe");
    }

    private async Task<(int Code, string Text)> ProbeAsync(string[] args, CancellationToken token, int seconds = 4)
    {
        var info = new ProcessStartInfo(Adb) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            token.ThrowIfCancellationRequested();
            return (-1, "Connection timed out");
        }
        return (process.ExitCode, (await output) + (await error));
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
                Stage("ONE-TIME APP INSTALL", "Install Steam", "Install Steam and sign in to continue.", 60,
                    "https://store.steampowered.com/about/", "Get Steam");
            else
                Stage("ONE-TIME APP INSTALL", $"Install {name}", $"Install {name} through Steam to continue.", 60,
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
            if (++attempts > 10) Stage("CLOSING VRCFACETRACKING", "Quit VRCFaceTracking", "Choose Quit from its system tray menu. It will reopen automatically.", 70);
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
                var stop = CalibrationForm.ReadJson(status)?["stopFile"]?.GetValue<string>();
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

    private async Task StopAsync()
    {
        if (_operation is not null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        Busy(true);
        try
        {
            Stage("STOPPING", "Stopping tracking", "Keep your headset connected.", 0);
            await StopHelpersAsync(operation.Token);
            _started = false;
            Stage("STOPPED", "Face tracking", "Stopped", 0);
            _go.Text = "Start";
        }
        catch (OperationCanceledException) { _message.Text = "Cleanup is still finishing in the background. Leave the headset connected."; }
        catch (Exception error) { Log(error.ToString()); _message.Text = "Couldn’t finish stopping. Keep the headset connected and try Stop tracking again."; }
        finally { _operation = null; Busy(false); }
    }

    internal static bool FaceDataReady(string mapName = "VirtualDesktop.BodyState")
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
            using var view = map.CreateViewAccessor(0, 360, MemoryMappedFileAccess.Read);
            return (view.ReadByte(0) & 1) != 0 && view.ReadByte(292) != 0 && view.ReadByte(293) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void UpdateLiveStatus()
    {
        if (_operation is not null) return;
        try
        {
            var status = CalibrationForm.ReadJson(Path.Combine(_root, "autostart-status.json"));
            using var process = Process.GetProcessById(status["pid"]!.GetValue<int>());
            if (process.HasExited) throw new InvalidOperationException("Tracking stopped");
            var state = status["state"]!.GetValue<string>();
            if (state == "running" && FaceDataReady())
            {
                _started = true;
                Stage("TRACKING IS RUNNING", "Tracking", "Connected over Wi-Fi. You can unplug USB and close this window.", 100);
                _go.Text = "Stop";
            }
            else if (_started || state == "waiting")
                Stage("WAITING FOR YOUR HEADSET", "Waiting for headset", "Wake your headset and connect in Virtual Desktop.", 0);
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException)
        {
            if (_started)
            {
                _started = false;
                Stage("TRACKING STOPPED", "Face tracking", "Disconnected", 0);
                _go.Text = "Start";
            }
        }
    }

    private string? RuntimeState()
    {
        try
        {
            var status = CalibrationForm.ReadJson(Path.Combine(_root, "autostart-status.json"));
            using var process = Process.GetProcessById(status["pid"]!.GetValue<int>());
            return process.HasExited ? null : status["state"]?.GetValue<string>();
        }
        catch (Exception error) when (error is IOException or JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    private void ShowDetails()
    {
        try
        {
            var details = new System.Text.StringBuilder();
            var logs = new[] { "setup.log", "autostart.log", "autostart-eyes.log", "autostart-tongue.log" }.Select(name => Path.Combine(_root, name));
            var captures = Path.Combine(_root, "captures");
            if (Directory.Exists(captures)) logs = logs.Concat(Directory.GetFiles(captures, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(1));
            foreach (var path in logs)
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                details.AppendLine(Path.GetFileName(path)).AppendLine(text.Length > 128000 ? text[^128000..] : text).AppendLine();
            }
            var output = Path.Combine(_root, "qpro-setup-details.txt");
            File.WriteAllText(output, details.Length == 0 ? "No setup has been run yet." : details.ToString());
            Open(output);
        }
        catch (Exception error) { MessageBox.Show(this, "Couldn’t open the details: " + error.Message); }
    }

    private void Log(string text) => File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception)
        { MessageBox.Show("Windows couldn’t open this item. Check that Steam is installed for a Steam link, or a web browser is available for a help link.", "Couldn’t open link"); }
    }
    private static Button MakeButton(string text) => new()
    {
        Text = text, UseMnemonic = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new(100, 36), Padding = new(10, 4, 10, 4), Margin = new(0, 4, 8, 4),
        UseVisualStyleBackColor = true,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
