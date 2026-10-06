using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Ui = Wpf.Ui.Controls;

namespace QFTPlus;

public partial class StudioWindow
{
    internal string kind = "enroll", runtimeId = "", candidate = "", prefix = "", trainingKind = "";
    internal JsonObject state = new();
    internal long command;
    internal bool recording, training, cancelPending;
    bool slow, polling, reloadPending;
    long recordingCommand;
    DateTime heartbeat = DateTime.MinValue;
    (bool Passed, string Message)? calibrationResult;
    WindowState? restoreState;
    long Ack => state["ack"]?.GetValue<long>() ?? 0;
    DateTime lastFresh = DateTime.UtcNow;
    bool FreshState() => state["time"]?.GetValue<double>() is { } time && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - time) < 3;
    static string GroupTitle(string kind) => kind == "pupils" ? "Pupils" : kind.StartsWith("benchmark") ? "Benchmark" : "Face";

    void DrawFace(JsonObject targets) =>
        face.Show(targets, poseTitle.Text, kind, !preview, recording ? state["index"]?.GetValue<int>() ?? -1 : -1, state["count"]?.GetValue<int>() ?? 0);

    void ResizeGuide()
    {
        if (page != "Calibration") return;
        face.Height = Math.Clamp(ActualHeight - 550, 100, 230);
        DrawFace(recording ? state["targets"]?.AsObject() ?? new() : new());
    }

    void RefreshCalibrationControls()
    {
        foreach (ListBoxItem item in Navigation.Items) item.IsEnabled = !recording;
        calibrationModes.IsEnabled = !recording && !training;
        slowMode.Visibility = Visible(kind == "enroll" && !recording && !training);
        begin.Visibility = Visible(!recording && !training);
        setUpFace.Visibility = Visible(SetupFirst && !recording && !training);
        pause.Visibility = skip.Visibility = Visible(recording && kind != "pupils");
        cancel.Visibility = Visible(recording);
        progress.Visibility = Visible(recording || training);
        StartButton.IsEnabled = !recording;
    }

    void Calibration()
    {
        if (HeadsetModelExperiment.Standalone(session))
        {
            Subtitle.Text = "Runs in QFT+ Headset, where an avatar shows each expression to copy.";
            Subtitle.Visibility = Visibility.Visible;
            var status = Muted("", live: true);
            var list = new StackPanel();
            var rows = new List<(TextBlock Value, string Kind, string About)>();
            foreach (var (kind, title, icon, about, primary) in new[] {
                         ("face", "Face and tongue", "sentiment_satisfied", "Copy 12 expressions from a Meta avatar · about 75 seconds", true),
                         ("pupils", "Pupil dilation", "adjust", "Follow a dot as the space darkens and brightens · about 80 seconds", false) })
            {
                var start = AsyncButton("Calibrate", async () =>
                {
                    try
                    {
                        session.App = await HeadsetApp.SendAsync(session, CancellationToken.None, ("calibrate", kind));
                        status.Text = "Calibration started in the headset. Put it on and copy the avatar. When it finishes, the headset goes back to the app you were in.";
                    }
                    catch (IOException error) { status.Text = error.Message; }
                });
                start.Appearance = primary ? Ui.ControlAppearance.Primary : Ui.ControlAppearance.Secondary;
                AutomationProperties.SetName(start, "Calibrate " + title.ToLowerInvariant());
                var value = Text("", 14, true);
                list.Children.Add(ActionRow(title, value, icon, start));
                rows.Add((value, kind, about));
            }
            RoundEnds(list);
            trackingRefresh += () =>
            {
                foreach (var (value, kind, about) in rows)
                    value.Text = (session.App is not { } app ? "" : HeadsetApp.Flag(app, "calibrated", kind) ? "Calibrated · " : "Not calibrated · ") + about;
                if (session.App is null) status.Text = session.AppProblem.Length > 0 ? session.AppProblem : "Reading calibration from the headset…";
            };
            trackingRefresh();
            var note = Muted("Calibrate after changing how the headset fits. Your saved calibration changes only when a new one passes.");
            note.Margin = new(0, 8, 0, 8);
            foreach (var element in new UIElement[] { list, note, status }) Page.Children.Add(element);
            return;
        }
        if (training) kind = trainingKind;
        calibrationModes.SelectionChanged -= CalibrationModeChanged;
        calibrationModes.Items.Clear();
        foreach (var (id, title) in new[] { ("enroll", "Face"), ("pupils", "Pupils") }
                     .Concat(session.Config["benchmarkMode"]?.GetValue<bool>() == true ? [("benchmark-quick", "Short benchmark"), ("benchmark", "Full benchmark")] : []))
            calibrationModes.Items.Add(new ComboBoxItem { Content = title, Tag = id, IsSelected = kind == id });
        calibrationModes.SelectionChanged += CalibrationModeChanged;
        poseTitle.Text = "Calibrate " + GroupTitle(kind).ToLowerInvariant();
        poseDetail.Text = (SetupFirst ? "Set up your face first, so the benchmark checks tracking that's tuned to you. It takes about a minute. " : "") + (kind switch
        {
            "enroll" => "Hold a few expressions for a few seconds each, so tracking learns your relaxed face and your full range.",
            "benchmark-quick" => "For testers. Short prompts and a little reading that check how well tracking works. Your settings don't change.",
            "benchmark" => "For testers. Prompts, reading and talking that check how often tracking reacts when it shouldn't. Your settings don't change.",
            _ => "Lets your avatar's pupils follow yours. Dim the room lights (or attach the light blockers) a few minutes before you start."
        }) + " To see this window in the headset, open the desktop in the Steam overlay.";
        cue.Text = kind switch { "pupils" => "About 80 seconds", "benchmark" => "About 18 minutes", "benchmark-quick" => "About 5 minutes", _ => EnrollLength };
        CalibrationCard.Content = CalibrationGuide;
        (progress.IsIndeterminate, progress.Value) = (false, 0);
        count.Text = kind == "pupils" ? "" : "Recordings stay on this PC.";
        count.Visibility = Visible(kind != "pupils");
        slowMode.IsChecked = slow;
        begin.Content = SetupFirst ? "Start without setup" : "Start calibration";
        begin.Appearance = SetupFirst ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
        pause.Content = "Pause";
        cancel.IsEnabled = true;
        review.Visibility = Visibility.Collapsed;
        foreach (var element in new UIElement[] { CalibrationAreaLabel, calibrationModes, CalibrationCard, slowMode }) Page.Children.Add(element);
        Actions.Children.Clear();
        foreach (var button in new[] { setUpFace, begin, pause, skip, cancel, review }) Actions.Children.Add(button);
        ResizeGuide();
        RefreshCalibrationControls();
        if (training && !trainingKind.StartsWith("benchmark")) ShowWorking();
        else if (calibrationResult is { } result && kind == trainingKind) Finished(result.Passed, result.Message);
    }

    void CalibrationReset()
    {
        Page.Children.Clear();
        Calibration();
    }

    void CalibrationModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (recording || training || calibrationModes.SelectedItem is not ComboBoxItem item) return;
        (kind, candidate, prefix, calibrationResult) = ((string)item.Tag, "", "", null);
        CalibrationReset();
    }

    bool SetupFirst => kind.StartsWith("benchmark") && !File.Exists(session.Config["faceEnrollment"]?.GetValue<string>());

    void SetUpFaceFirst(object sender, RoutedEventArgs e)
    {
        if (recording || training) return;
        (kind, candidate, prefix, calibrationResult) = ("enroll", "", "", null);
        CalibrationReset();
    }

    string EnrollLength => slow ? "About 2 minutes" : "About 1 minute";
    void BeginCalibration(object sender, RoutedEventArgs e) => Begin();
    void GuideCommand(object sender, RoutedEventArgs e) => Send((string)((Button)sender).Tag);
    void ReviewCalibration(object sender, RoutedEventArgs e) => Open(prefix + ".avi");

    void SlowModeClick(object sender, RoutedEventArgs e)
    {
        if (recording || training) return;
        slow = slowMode.IsChecked == true;
        cue.Text = EnrollLength;
    }

    void CancelCalibration(object sender, RoutedEventArgs e)
    {
        cancelPending = true;
        cancel.IsEnabled = false;
        cue.Text = "Stopping calibration…";
    }

    internal void Begin()
    {
        if (recording || training) return;
        if (HeadsetModelExperiment.Standalone(session))
        {
            Error("Open Calibration in QFT+ Headset. This experiment records and processes calibration on the headset.");
            return;
        }
        if (!session.Running) Error("Start tracking, then start calibration.");
        else if (runtimeId.Length == 0 || session.State != "Connected" || !FreshState()) Error("Waiting for the headset cameras. Make sure the headset is awake and connected.");
        else
        {
            ClearNotice();
            (candidate, prefix) = ("", "");
            if (!Send("begin", new JsonObject { ["kind"] = kind, ["slow"] = slow })) return;
            (recordingCommand, cancelPending, recording, calibrationResult) = (command, false, true, null);
            CalibrationReset();
            count.Visibility = Visibility.Visible;
            if (kind != "pupils" || WindowState == WindowState.Maximized) return;
            restoreState = WindowState;
            WindowState = WindowState.Maximized;
        }
    }

    internal bool Send(string action, JsonObject? fields = null)
    {
        if (runtimeId.Length == 0) return false;
        if (Ack < command)
        {
            if (action == "reload") reloadPending = true;
            else if (action != "heartbeat") Error("Waiting for the previous action…");
            return false;
        }
        var data = fields ?? new JsonObject();
        (data["id"], data["runtimeId"], data["action"]) = (command + 1, runtimeId, action);
        CalibrationSettings.WriteJson(Path.Combine(session.Root, "studio.command.json"), data);
        command++;
        heartbeat = DateTime.UtcNow;
        return true;
    }

    async Task Conclude(Func<Task<string>> work, string title = "Creating your calibration", string detail = "This takes a few seconds. Tracking stays on.")
    {
        (training, trainingKind) = (true, kind);
        ClearNotice();
        RefreshCalibrationControls();
        ShowWorking(title, detail);
        (bool Passed, string Message) result;
        try { result = (true, await work()); }
        catch (Exception error)
        {
            result = trainingKind.StartsWith("benchmark") ? (true, "Benchmark recorded, but it couldn’t be prepared to share. " + error.Message) : (false, error.Message);
        }
        training = false;
        progress.IsIndeterminate = false;
        Finished(result.Passed, result.Message);
    }

    void ShowWorking(string title = "Creating your calibration", string detail = "This takes a few seconds. Tracking stays on.")
    {
        if (page != "Calibration" || !training) return;
        (poseTitle.Text, poseDetail.Text, cue.Text) = (title, detail, "");
        progress.IsIndeterminate = true;
        progress.Visibility = Visibility.Visible;
        count.Visibility = Visibility.Collapsed;
    }

    void Apply(string which)
    {
        if (candidate.Length == 0) return;
        session.Apply(which, candidate);
        Send("reload");
    }

    internal void Finished(bool passed, string message)
    {
        calibrationResult = (passed, message);
        RefreshCalibrationControls();
        if (page != "Calibration" || kind != trainingKind)
        {
            Error(passed ? message : GroupTitle(trainingKind) + " calibration wasn't saved. Open Calibration for details.");
            return;
        }
        CalibrationCard.Content = CalibrationResultPanel;
        var benchmark = passed && trainingKind.StartsWith("benchmark");
        ResultTitle.Text = benchmark ? "Benchmark recorded" : passed ? "Calibration is on" : "Calibration wasn't saved";
        ResultDetail.Text = benchmark ? message : passed ? "Check your avatar to see the difference." : "Your current calibration hasn't changed.";
        ResultFiles.Visibility = Visible(benchmark && File.Exists(candidate));
        var lines = passed ? [] : message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ResultError.Text = lines.FirstOrDefault() ?? "Try recording your poses again.";
        ResultError.Visibility = Visible(!passed);
        ResultFindings.IsExpanded = false;
        ResultLines.ItemsSource = lines.Skip(1);
        ResultDetailsHost.Children.Clear();
        if (lines.Length > 1) ResultDetailsHost.Children.Add(ResultFindings);
        begin.Content = passed ? "Calibrate again" : "Try again";
        begin.Appearance = passed ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
        review.Visibility = Visible(File.Exists(prefix + ".avi"));
        UIElementAutomationPeer.FromElement(ResultTitle)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    void ShowBenchmarkFile(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{candidate}\"").Dispose(); }
        catch (Exception) { Error("Couldn’t open File Explorer. The file is in " + Path.GetDirectoryName(candidate) + "."); }
    }

    internal async Task Tick()
    {
        if (polling) return;
        polling = true;
        try
        {
            if (manualTesting)
            {
                if (page == "Manual" && IsVisible) PublishManual();
                else StopManual();
            }
            if (!busy) session.Poll();
            try { state = Session.Read(Path.Combine(session.Root, "studio.state.json")); }
            catch (InvalidDataException) { state = new(); }
            var id = state["runtimeId"]?.GetValue<string>() ?? "";
            var restarted = id != runtimeId;
            if (restarted) (runtimeId, command) = (id, Ack);
            if (FreshState() || !recording) lastFresh = DateTime.UtcNow;
            if (recording && (!session.Running || restarted || DateTime.UtcNow - lastFresh > TimeSpan.FromSeconds(15)))
            {
                (recording, cancelPending) = (false, false);
                Theme("System");
                if (restoreState is { } before) (WindowState, restoreState) = (before, null);
                if (page == "Calibration") CalibrationReset(); else RefreshCalibrationControls();
                Error("Calibration stopped because tracking stopped or the camera feed stayed paused. Your previous calibration is unchanged.");
            }
            var settled = FreshState() && Ack >= command;
            if (cancelPending && settled && Send("cancel"))
            {
                (cancelPending, recordingCommand) = (false, command);
                ClearNotice();
            }
            if (reloadPending && settled)
            {
                reloadPending = false;
                Send("reload");
            }
            if (recording && !cancelPending && DateTime.UtcNow - heartbeat > TimeSpan.FromMilliseconds(700)) Send("heartbeat");
            if (page == "Calibration" && recording) UpdateGuide();
            if (recording && !FreshState()) Error("Camera feed paused. Make sure the headset is awake and connected, or cancel.");
            if (page == "Cameras" && IsVisible) await UpdateCameras();
            if (IsVisible) adjustmentRefresh?.Invoke();
            if (state["error"]?.GetValue<string>() is { Length: > 0 } error && (recording ? Ack >= recordingCommand : page == "Calibration")) Error(error);
            if (page != "Tracking" && session.Hybrid is not null && !Session.Alive(session.Hybrid)) Error("Hybrid hands stopped. The Hybrid hands log in Settings shows why.");
        }
        catch (Exception error) { Error(error.Message); }
        finally { polling = false; }
    }

    void UpdateGuide()
    {
        if (cancelPending || !FreshState() || Ack < recordingCommand || state["kind"]?.GetValue<string>() != kind) return;
        poseTitle.Text = state["title"]?.GetValue<string>() ?? poseTitle.Text;
        poseDetail.Text = state["instruction"]?.GetValue<string>() ?? "";
        cue.Text = kind == "pupils" ? $"{state["remaining"]?.GetValue<double>():0} seconds" : state["cue"]?.GetValue<string>() ?? "Look at the circle";
        var index = state["index"]?.GetValue<int>() ?? 0;
        progress.Value = (state["progress"]?.GetValue<double>() ?? index / 4.0) * 100;
        count.Text = $"{index + 1} of {state["total"]?.GetValue<int>() ?? 4}";
        if (kind == "pupils") SetPupilStimulus(state["level"]?.GetValue<double>() ?? 0);
        else DrawFace(state["targets"]?.AsObject() ?? new());
        pause.Content = state["paused"]?.GetValue<bool>() == true ? "Resume" : "Pause";
        if (state["phase"]?.GetValue<string>() is not ("complete" or "cancelled")) return;
        recording = false;
        Theme("System");
        if (restoreState is { } before) (WindowState, restoreState) = (before, null);
        if (state["phase"]!.GetValue<string>() != "complete")
        {
            CalibrationReset();
            return;
        }
        prefix = state["prefix"]!.GetValue<string>();
        progress.Value = 100;
        count.Text = "";
        RefreshCalibrationControls();
        if (kind == "pupils")
        {
            (candidate, trainingKind) = (prefix + ".pupils.json", "pupils");
            try
            {
                Apply("pupils");
                Finished(true, "Pupil calibration is on.");
            }
            catch (Exception error) { Finished(false, error.Message); }
        }
        else if (!kind.StartsWith("benchmark"))
        {
            if (prefix.Length > 0)
                _ = Conclude(async () =>
                {
                    candidate = await session.Enroll(prefix);
                    Apply("enroll");
                    return "Face calibration is on.";
                });
        }
        else if (!session.Tester)
        {
            (candidate, trainingKind) = ("", kind);
            Finished(true, "Benchmark recorded. Its files start with " + Path.GetFileName(prefix) + " in " + Path.GetDirectoryName(prefix) + ".");
        }
        else
        {
            candidate = "";
            _ = Conclude(async () =>
            {
                candidate = await session.Package(prefix);
                return $"Send this file to the QFT+ developer directly: {Path.GetFileName(candidate)} ({new FileInfo(candidate).Length / 1e6:0} MB). "
                    + "If it’s too big to attach to a message, send a OneDrive or Google Drive link to it instead.";
            }, "Preparing your recording", "This takes about a minute. Tracking stays on.");
        }
    }

    void SetPupilStimulus(double level)
    {
        var gray = (byte)Math.Round(18 + Math.Clamp(level, 0, 1) * 217);
        Resources["Canvas"] = Resources["Surface"] = Resources["Sidebar"] = new SolidColorBrush(Color.FromRgb(gray, gray, gray));
        Resources["Ink"] = Resources["Muted"] = level > .5 ? Brushes.Black : Brushes.White;
        DrawFace(new());
    }
}
