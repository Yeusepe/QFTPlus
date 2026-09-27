using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace QproFaceTracking.Hub;

internal sealed class CalibrationForm : Form
{
    private readonly string _root, _kind, _prefix, _capture, _pupil, _stop, _configPath;
    private readonly Label _title = new() { AutoSize = true, Dock = DockStyle.Top, Font = new("Segoe UI", 18, FontStyle.Bold), Margin = new(0, 0, 0, 14) };
    private readonly Label _instruction = new() { AutoSize = true, Dock = DockStyle.Top, Margin = new(0, 0, 0, 20) };
    private readonly Label _status = new() { AutoSize = true, Dock = DockStyle.Top, Margin = new(0, 0, 0, 12), AccessibleRole = AccessibleRole.StatusBar };
    private readonly Label _cross = new() { Text = "+", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new("Segoe UI", 40), Visible = false, AccessibleName = "Look at the center cross" };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 8, AccessibleName = "Calibration progress", MarqueeAnimationSpeed = SystemInformation.UIEffectsEnabled ? 30 : 0, Visible = false, Margin = new(0, 0, 0, 20) };
    private readonly Button _primary = Button("Begin");
    private readonly Button _next = Button("Next");
    private readonly Button _undo = Button("Undo capture");
    private readonly Button _back = Button("Back");
    private readonly Button _cancel = Button("Cancel");
    private readonly TableLayoutPanel _page = new() { Dock = DockStyle.Fill, Padding = new(28), ColumnCount = 1, AutoScroll = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private readonly CancellationTokenSource _cancellation = new();
    private Task<string>? _captureTask;
    private JsonObject? _state;
    private string? _result;
    private string? _resume;
    private int _command, _trainingStage = 1;
    private bool _busy, _finishing, _closeRequested, _applying;
    private DateTime _lastState = DateTime.UtcNow;

    public CalibrationForm(string root, string kind, string? configPath = null)
    {
        _root = root; _kind = kind;
        _configPath = configPath ?? EasySetupForm.AutoPath;
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        _prefix = Path.Combine(root, "captures", kind + "-" + id);
        _capture = _prefix + ".qpcap"; _pupil = _prefix + ".pupils.json"; _stop = _prefix + ".stop";
        Text = kind switch { "tongue" => "Calibrate tongue", "puff" => "Calibrate cheek puff", _ => "Calibrate pupils" };
        Font = new("Segoe UI", 12);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(660, kind == "pupils" ? 430 : 330);
        MinimumSize = new(560, kind == "pupils" ? 430 : 350);
        StartPosition = FormStartPosition.CenterParent;
        FontChanged += (_, _) => _title.Font = new(Font.FontFamily, Font.Size * 1.5f, FontStyle.Bold);
        _page.ColumnStyles.Add(new(SizeType.Percent, 100));
        _page.Controls.Add(_title); _page.Controls.Add(_instruction);
        _page.Controls.Add(_status); _page.Controls.Add(_progress);
        _page.Controls.Add(_cross);
        _page.RowStyles.Add(new(SizeType.AutoSize)); _page.RowStyles.Add(new(SizeType.AutoSize));
        _page.RowStyles.Add(new(SizeType.AutoSize)); _page.RowStyles.Add(new(SizeType.AutoSize));
        _page.RowStyles.Add(new(SizeType.Percent, 100));
        _page.RowStyles.Add(new(SizeType.AutoSize));
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, Margin = new(0) };
        actions.Controls.AddRange([_primary, _next, _undo, _back, _cancel]);
        _page.Controls.Add(actions);
        if (kind == "pupils") Controls.Add(_page);
        else
        {
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            _page.Dock = DockStyle.Top; _page.AutoSize = true;
            scroll.Controls.Add(_page); Controls.Add(scroll);
        }
        _title.Text = kind switch { "tongue" => "Teach your expressions", "puff" => "Calibrate each cheek", _ => "Calibrate pupil size" };
        _instruction.Text = kind switch
        {
            "tongue" => "Open this window in Virtual Desktop. Follow each pose and capture six samples. Include the smiles and open-mouth poses that trigger your tongue.",
            "puff" => "Open this window in Virtual Desktop. Follow three rounds of cheek poses. Left and right mean your own sides.",
            _ => "Open this window in Virtual Desktop. Look at the center cross while the screen changes brightness for 56 seconds."
        };
        _status.Text = kind == "pupils" ? "The window will maximize when you begin." : "Captures stay on this PC. Relax and reform the pose between samples.";
        _next.Visible = _undo.Visible = _back.Visible = false;
        if (kind == "tongue" && Directory.Exists(Path.Combine(root, "captures")))
        {
            foreach (var saved in Directory.GetFiles(Path.Combine(root, "captures"), "*.qpsession.json").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(saved) > TimeSpan.FromHours(24)) break;
                try
                {
                    var session = ReadJson(saved);
                    if (session["sessionType"]?.GetValue<string>() != "tongue-stereo-refinement-v2" || session["completed"]?.GetValue<bool>() != false || session["samples"]?.AsArray().Count is not > 0) continue;
                    _resume = saved;
                    _title.Text = "Continue your calibration";
                    _instruction.Text = $"Your captures are saved. Continue at pose {session["currentPrompt"]!.GetValue<int>() + 1} of 14, with the headset fitted the same way.";
                    _primary.Text = "Continue";
                    _back.Text = "Start over"; _back.Visible = true;
                    break;
                }
                catch (Exception error) when (error is IOException or JsonException or InvalidOperationException) { }
            }
        }
        AcceptButton = _primary;
        CancelButton = _cancel;
        _primary.Click += async (_, _) =>
        {
            if (_result is not null) await ApplyAsync();
            else if (!_busy) await BeginAsync();
            else Send(_kind == "pupils" ? "c" : " ");
        };
        _next.Click += (_, _) => Send("\r");
        _undo.Click += (_, _) => Send("x");
        _back.Click += (_, _) =>
        {
            if (_captureTask is not null) Send("b");
            else { _resume = null; _back.Visible = false; _primary.Text = "Begin"; _title.Text = "Teach your expressions"; _instruction.Text = "Open this window in Virtual Desktop and follow each pose."; }
        };
        _cancel.Click += (_, _) => Close();
        _timer.Tick += async (_, _) => await TickAsync();
        FormClosing += (_, e) =>
        {
            if (!_busy || _applying) return;
            e.Cancel = true;
            _closeRequested = true;
            _cancellation.Cancel();
            StopCapture();
            _cancel.Enabled = _primary.Enabled = _next.Enabled = _undo.Enabled = _back.Enabled = false;
            _status.Text = "Stopping… Keep the headset connected.";
        };
    }

    private async Task BeginAsync()
    {
        _busy = true;
        _primary.Enabled = false;
        _title.Text = "Connecting cameras";
        _instruction.Text = "Keep your headset awake and connected.";
        _status.Text = "";
        _progress.Visible = true; _progress.Style = ProgressBarStyle.Marquee;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_prefix)!);
            var arguments = new List<string> { "-SkipPythonSetup", "-NoWindow", "-CalibrationUi", _prefix, "-StopFile", _stop };
            _back.Text = "Back"; _back.Visible = false;
            if (_resume is not null) arguments.AddRange(["-ResumeSession", _resume]);
            if (EasySetupForm.ReadWireless(_root) is { } wireless) arguments.AddRange(["-AdbTarget", wireless.Target]);
            if (_kind == "tongue") arguments.AddRange(["-TongueRefinementCalibration", "-RecordPath", _capture]);
            else if (_kind == "puff") arguments.AddRange(["-ExtraFaceCapture", "puff", "-RecordPath", _capture, "-NoLabels"]);
            else arguments.AddRange(["-PupilPreview", "-PupilCalibrationPath", _pupil, "-NoLabels"]);
            _captureTask = RunAsync("build-and-run.ps1", arguments.ToArray());
            _lastState = DateTime.UtcNow;
            _timer.Start();
            await Task.CompletedTask;
        }
        catch (Exception error) { Failed(error); }
    }

    private void Send(string key)
    {
        if (_state is null || _finishing || _cancellation.IsCancellationRequested) return;
        try
        {
            var path = _prefix + ".command.json";
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { id = ++_command, key }));
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(path + ".tmp", path, true); break; }
                catch (IOException) when (attempt < 9) { Thread.Sleep(10); }
            }
            _primary.Enabled = _next.Enabled = _undo.Enabled = _back.Enabled = false;
            if (key == "c") WindowState = FormWindowState.Maximized;
        }
        catch (Exception error) { _command--; _instruction.Text = "The action wasn’t sent. Try again. " + error.Message; }
    }

    private async Task TickAsync()
    {
        if (_finishing || _captureTask is null) return;
        try
        {
            var path = _prefix + ".state.json";
            try
            {
                if (File.Exists(path))
                {
                    _state = ReadJson(path);
                    _lastState = DateTimeOffset.FromUnixTimeMilliseconds((long)(_state["time"]!.GetValue<double>() * 1000)).UtcDateTime;
                    if (!_closeRequested) DisplayState(_state);
                }
            }
            catch (IOException) { }
            catch (JsonException) { }
            if (DateTime.UtcNow - _lastState > TimeSpan.FromSeconds(2))
                _primary.Enabled = _next.Enabled = _undo.Enabled = _back.Enabled = false;
            if (_state?["completed"]?.GetValue<bool>() == true || _captureTask.IsCompleted)
            {
                _finishing = true; _timer.Stop();
                StopCapture();
                await _captureTask;
                _cancellation.Token.ThrowIfCancellationRequested();
                var journal = Path.ChangeExtension(_capture, ".qpsession.json");
                if (_kind != "pupils" && File.Exists(journal) && ReadJson(journal)["completed"]?.GetValue<bool>() == true)
                {
                    _state ??= new();
                    _state["completed"] = true;
                }
                if (_state?["completed"]?.GetValue<bool>() != true)
                    throw new IOException("Capture stopped before calibration finished. Your previous calibration is unchanged.");
                await TrainAsync();
                _busy = false;
                _finishing = false;
                _title.Text = "Ready to use";
                _instruction.Text = _kind == "tongue" ? "After applying, test smiles and open-mouth poses with your tongue hidden, then each tongue direction." : _kind == "puff" ? "The recorded test round passed. Check each cheek in your avatar after applying." : "Your bright and dim pupil responses passed the repeat check.";
                _status.Text = "";
                _cross.Visible = _progress.Visible = _next.Visible = _undo.Visible = _back.Visible = false;
                ResetColors();
                _primary.Text = "Use calibration"; _primary.Enabled = true;
                _cancel.Text = "Discard";
                AcceptButton = _primary;
            }
            else if (DateTime.UtcNow - _lastState > TimeSpan.FromSeconds(45))
            {
                _finishing = true;
                StopCapture();
                await _captureTask;
                throw new IOException("Camera data stopped. Reconnect your headset in Virtual Desktop and try again.");
            }
        }
        catch (OperationCanceledException) { _busy = false; Close(); }
        catch (Exception error)
        {
            _finishing = true;
            StopCapture();
            try { await _captureTask; } catch { }
            Failed(error);
        }
    }

    internal void DisplayState(JsonObject state)
    {
        _progress.Visible = true;
        bool ready = state["ready"]?.GetValue<bool>() == true && state["ack"]!.GetValue<int>() >= _command;
        _progress.Style = ProgressBarStyle.Blocks;
        if (_kind == "pupils")
        {
            var phase = state["phase"]?.GetValue<int>();
            _cross.Visible = phase is not null;
            _primary.Visible = phase is null;
            _primary.Text = state["result"]?.GetValue<string>() == "failed" ? "Try again" : "Begin";
            _primary.Enabled = ready;
            if (phase is not null)
            {
                var bright = state["bright"]!.GetValue<bool>();
                _page.BackColor = Color.FromArgb(bright ? 235 : 18, bright ? 235 : 18, bright ? 235 : 18);
                _page.ForeColor = bright ? Color.FromArgb(25, 25, 25) : Color.FromArgb(220, 220, 220);
                _title.Text = "Look at the center cross";
                _instruction.Text = "Keep your head still. Blink normally.";
                _status.Text = $"{phase + 1} of 4 · {state["remaining"]} seconds";
                _progress.Value = (phase.Value * 25);
            }
            else
            {
                ResetColors();
                _title.Text = "Calibrate pupil size";
                _instruction.Text = "View this window inside your headset. Select Begin when you can see it clearly.";
                _status.Text = state["result"]?.GetValue<string>() == "failed" ? state["message"]!.GetValue<string>().Replace(". Press C to retry.", ".") : "56 seconds";
            }
            return;
        }
        var index = state["index"]!.GetValue<int>();
        var total = state["total"]!.GetValue<int>();
        var count = state["count"]!.GetValue<int>();
        var recommended = state["recommended"]!.GetValue<int>();
        _title.Text = state["title"]!.GetValue<string>();
        _instruction.Text = state["instruction"]!.GetValue<string>().Replace(" Skip with K if you cannot isolate this pose.", "");
        _status.Text = ready ? $"Pose {index + 1} of {total} · {count} of {recommended} captures" : "Move your face briefly to start tracking.";
        _progress.Value = Math.Clamp((index * 100 + Math.Min(count, recommended) * 100 / recommended) / total, 0, 100);
        _primary.Text = "Capture";
        _primary.Enabled = ready && state["pending"]?.GetValue<bool>() != true;
        _next.Visible = _undo.Visible = _back.Visible = true;
        _next.Enabled = ready && count >= recommended && state["pending"]?.GetValue<bool>() != true;
        _undo.Enabled = ready && count > 0 && state["pending"]?.GetValue<bool>() != true;
        _back.Enabled = ready && index > 0 && state["pending"]?.GetValue<bool>() != true;
        _next.Text = index == total - 1 ? "Finish" : "Next";
        AcceptButton = _next.Enabled ? _next : _primary;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Space && _busy && !_finishing && _kind != "pupils" && _primary.Enabled)
        { Send(" "); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task TrainAsync()
    {
        _next.Visible = _undo.Visible = _back.Visible = _primary.Visible = _cross.Visible = false;
        ResetColors();
        _title.Text = "Learning your expressions";
        _instruction.Text = "You can take off the headset.";
        _status.Text = "Preparing captures…";
        _progress.Style = ProgressBarStyle.Marquee;
        if (_kind == "tongue")
        {
            var arguments = new List<string> { "-SessionPath", Path.ChangeExtension(_capture, ".qpsession.json") };
            if (File.Exists(_configPath))
            {
                var config = JsonNode.Parse(File.ReadAllText(_configPath))!;
                var gate = config["tongueModelPath"]?.GetValue<string>();
                var direction = config["tongueDirectionModelPath"]?.GetValue<string>();
                if (File.Exists(gate) && File.Exists(direction)) arguments.AddRange(["-BaseGatePath", gate!, "-BaseDirectionPath", direction!]);
            }
            var output = await RunAsync("train-latest-tongue-refinement.ps1", arguments.ToArray(), training: true);
            var match = Regex.Match(output, @"MODEL_READY version=(\d+)");
            if (!match.Success) throw new IOException("Training did not produce a complete model pair.");
            _result = Path.Combine(_root, "models", "qpro-stereo-tongue-v" + match.Groups[1].Value);
        }
        else if (_kind == "puff")
        {
            var model = Path.Combine(_root, "models", Path.GetFileName(_prefix) + ".pt");
            await RunAsync("train_extra_face.py", [_capture, "--output", model], training: true);
            try { await RunAsync("calibration_ui.py", ["check-puff", model], training: true); }
            catch (IOException) { throw new IOException("The test poses were not distinct enough. Repeat calibration, relaxing the other cheek."); }
            _result = model;
        }
        else _result = _pupil;
        _primary.Visible = true;
    }

    private async Task ApplyAsync()
    {
        if (_result is null || _applying) return;
        _applying = true; _primary.Enabled = _cancel.Enabled = false;
        try
        {
            if (_kind == "puff") await RunAsync("calibration_ui.py", ["approve-puff", _result], training: true);
            SaveCalibration(_root, _kind, _result, _configPath);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception error) { Failed(error); }
        finally { _applying = false; _cancel.Enabled = true; }
    }

    internal static void SaveCalibration(string root, string kind, string result, string configPath)
    {
        var config = File.Exists(configPath) ? JsonNode.Parse(File.ReadAllText(configPath))!.AsObject() : new JsonObject();
        var pupilPath = Path.Combine(root, "calibration/qpro-pupil-dilation.json");
        byte[]? previousPupil = null;
        if (kind == "tongue")
        {
            if (!File.Exists(result + "-gate.pt") || !File.Exists(result + "-direction.pt")) throw new IOException("The new tongue model pair is incomplete.");
            config["tongueModelPath"] = result + "-gate.pt";
            config["tongueDirectionModelPath"] = result + "-direction.pt";
        }
        else if (kind == "puff")
        {
            if (!File.Exists(result)) throw new IOException("The cheek model is missing.");
            config["extraFaceModel"] = result; config["extraFaceOutput"] = true;
        }
        else if (kind == "pupils")
        {
            if (JsonNode.Parse(File.ReadAllText(result))?["format"]?.GetValue<string>() != "qpro-relative-pupil-v1") throw new IOException("The pupil calibration is invalid.");
            previousPupil = File.Exists(pupilPath) ? File.ReadAllBytes(pupilPath) : null;
            config["pupilDilation"] = true;
        }
        else throw new ArgumentException("Unknown calibration type.");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        if (File.Exists(configPath)) File.Copy(configPath, configPath + ".before-calibration", true);
        try
        {
            if (kind == "pupils")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(pupilPath)!);
                if (previousPupil is not null) File.WriteAllBytes(pupilPath + ".previous", previousPupil);
                File.Copy(result, pupilPath + ".tmp", true);
                File.Move(pupilPath + ".tmp", pupilPath, true);
            }
            File.WriteAllText(configPath + ".tmp", config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(configPath + ".tmp", configPath, true);
        }
        catch
        {
            if (kind == "pupils")
            {
                if (previousPupil is not null) File.WriteAllBytes(pupilPath, previousPupil);
                else File.Delete(pupilPath);
            }
            throw;
        }
    }

    private async Task<string> RunAsync(string script, string[] arguments, bool training = false)
    {
        var python = script.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(python ? EasySetupForm.FindPython(_root) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
        { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!python) foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File" }) info.ArgumentList.Add(arg);
        info.ArgumentList.Add(Path.Combine(_root, script));
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        info.Environment.Remove("PSModulePath");
        info.Environment["QPRO_ADB"] = Path.Combine(_root, "platform-tools/adb.exe");
        info.Environment["QPRO_PYTHON"] = EasySetupForm.FindPython(_root);
        info.Environment["PYTHONUNBUFFERED"] = "1";
        using var process = Process.Start(info) ?? throw new IOException("Could not start calibration.");
        using var registration = _cancellation.Token.Register(() =>
        {
            if (training) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            else StopCapture();
        });
        var output = new StringBuilder();
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                output.AppendLine(line);
                try { File.AppendAllText(_prefix + ".log", line + Environment.NewLine); }
                catch (IOException) { }
                if (!training || _closeRequested) continue;
                var stage = Regex.Match(line, @"TRAIN_STAGE index=(\d+)");
                if (stage.Success) _trainingStage = int.Parse(stage.Groups[1].Value);
                var epoch = Regex.Match(line, @"(?:TRAIN_EPOCH current=|Epoch )(\d+)(?: total=|/)(\d+)");
                if (epoch.Success)
                {
                    var percent = int.Parse(epoch.Groups[1].Value) * 100 / int.Parse(epoch.Groups[2].Value);
                    _progress.Style = ProgressBarStyle.Blocks;
                    _progress.Value = Math.Clamp(_kind == "tongue" ? ((_trainingStage - 1) * 100 + percent) / 2 : percent, 0, 100);
                    _status.Text = $"Training · {_progress.Value}%";
                }
            }
        }
        await Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError), process.WaitForExitAsync());
        _cancellation.Token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException(training ? "Training didn’t finish. Your previous calibration is unchanged. Open Help → View logs for details." : "Couldn’t read the headset cameras. Reconnect in Virtual Desktop and try again.");
        return output.ToString();
    }

    private void StopCapture()
    {
        if (_captureTask is { IsCompleted: false }) File.WriteAllText(_stop, "stop");
    }

    private void ResetColors() { _page.ResetBackColor(); _page.ResetForeColor(); _cross.Visible = false; }

    internal static JsonObject ReadJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonNode.Parse(stream)!.AsObject();
    }

    private void Failed(Exception error)
    {
        _timer.Stop();
        _busy = _finishing = false;
        ResetColors();
        _title.Text = "Calibration didn’t finish";
        _instruction.Text = error.Message;
        _status.Text = "Your previous calibration is unchanged.";
        _progress.Visible = _primary.Visible = _next.Visible = _undo.Visible = _back.Visible = false;
        _cancel.Text = "Close"; _cancel.Enabled = true;
    }

    private static Button Button(string text) => new() { Text = text, AutoSize = true, MinimumSize = new(90, 38), Padding = new(8, 4, 8, 4), Margin = new(0, 4, 8, 4), UseVisualStyleBackColor = true };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _cancellation.Dispose(); }
        base.Dispose(disposing);
    }
}
