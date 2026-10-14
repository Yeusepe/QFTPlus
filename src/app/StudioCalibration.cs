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
    static string GroupTitle(string kind) => kind == "pupils" ? Loc.Get("Pupils") : kind.StartsWith("benchmark") ? Loc.Get("Benchmark") : Loc.Get("Face");

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
            Subtitle.Text = Loc.Get("RunsInQFTHeadsetWhereAnAvatarShowsEach");
            Subtitle.Visibility = Visibility.Visible;
            var status = Muted("", live: true);
            var list = new StackPanel();
            var rows = new List<(TextBlock Value, string Kind, string About)>();
            var watching = false;
            foreach (var (kind, title, icon, about, primary) in new[] {
                         ("face", Loc.Get("FaceAndTongue"), "sentiment_satisfied", Loc.Get("Copy12ExpressionsFromAMetaAvatarAbout75"), true),
                         ("pupils", Loc.Get("PupilDilation"), "adjust", Loc.Get("FollowADotAsTheSpaceDarkensAndBrightens"), false) })
            {
                var start = AsyncButton(Loc.Get("Calibrate_9604ba0c"), async () =>
                {
                    try
                    {
                        session.App = await session.SendApp(CancellationToken.None, ("calibrate", kind));
                        watching = true;
                        status.Text = Loc.Get("CalibrationStartedInTheHeadsetPutItOnAnd");
                    }
                    catch (IOException error) { status.Text = error.Message; }
                });
                start.Appearance = primary ? Ui.ControlAppearance.Primary : Ui.ControlAppearance.Secondary;
                AutomationProperties.SetName(start, Loc.Format("CalibrateGroup", title));
                var value = Text("", 14, true);
                list.Children.Add(ActionRow(title, value, icon, start));
                rows.Add((value, kind, about));
            }
            RoundEnds(list);
            trackingRefresh += () =>
            {
                foreach (var (value, kind, about) in rows)
                    value.Text = session.App is not { } app ? about : Loc.Format("CalibrationStatusDetails", Loc.Get(HeadsetApp.Flag(app, "calibrated", kind) ? "Calibrated" : "NotCalibrated"), about);
                if (session.App is null) status.Text = session.AppProblem.Length > 0 ? session.AppProblem : Loc.Get("ReadingCalibrationFromTheHeadset");
                else if (session.App["calibration"] is JsonObject headset)
                {
                    var calibrating = Loc.Get("CalibratingInTheHeadset");
                    var result = headset["result"]?.GetValue<string>() ?? "";
                    if (headset["running"]?.GetValue<bool>() == true) (status.Text, watching) = (calibrating, true);
                    else if (watching && (result.Length > 0 || status.Text == calibrating)) (status.Text, watching) = (result, false);
                }
            };
            trackingRefresh();
            var note = Muted(Loc.Get("CalibrateAfterChangingHowTheHeadsetFitsYourSaved"));
            note.Margin = new(0, 8, 0, 8);
            foreach (var element in new UIElement[] { list, note, status }) Page.Children.Add(element);
            return;
        }
        if (training) kind = trainingKind;
        calibrationModes.SelectionChanged -= CalibrationModeChanged;
        calibrationModes.Items.Clear();
        foreach (var (id, title) in new[] { ("enroll", Loc.Get("Face")), ("pupils", Loc.Get("Pupils")) }
                     .Concat(session.Config["benchmarkMode"]?.GetValue<bool>() == true ? [("benchmark-quick", Loc.Get("ShortBenchmark")), ("benchmark", Loc.Get("FullBenchmark"))] : []))
            calibrationModes.Items.Add(new ComboBoxItem { Content = title, Tag = id, IsSelected = kind == id });
        calibrationModes.SelectionChanged += CalibrationModeChanged;
        poseTitle.Text = Loc.Format("CalibrateGroup", GroupTitle(kind));
        var instructions = kind switch { "enroll" => "Face", "benchmark-quick" => "QuickBenchmark", "benchmark" => "Benchmark", _ => "Pupil" };
        poseDetail.Text = Loc.Get(instructions + "CalibrationInstructions" + (SetupFirst ? "NeedsSetup" : ""));
        cue.Text = kind switch { "pupils" => Loc.Get("About80Seconds"), "benchmark" => Loc.Get("About18Minutes"), "benchmark-quick" => Loc.Get("About5Minutes"), _ => EnrollLength };
        CalibrationCard.Content = CalibrationGuide;
        (progress.IsIndeterminate, progress.Value) = (false, 0);
        count.Text = kind == "pupils" ? "" : Loc.Get("RecordingsStayOnThisPC");
        count.Visibility = Visible(kind != "pupils");
        slowMode.IsChecked = slow;
        begin.Content = SetupFirst ? Loc.Get("StartWithoutSetup") : Loc.Get("StartCalibration");
        begin.Appearance = SetupFirst ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
        pause.Content = Loc.Get("Pause");
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

    string EnrollLength => slow ? Loc.Get("About2Minutes") : Loc.Get("About1Minute");
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
        cue.Text = Loc.Get("StoppingCalibration");
    }

    internal void Begin()
    {
        if (recording || training) return;
        if (HeadsetModelExperiment.Standalone(session))
        {
            Error(Loc.Get("OpenCalibrationInQFTHeadsetThisExperimentRecordsAnd"));
            return;
        }
        if (!session.Running) Error(Loc.Get("StartTrackingThenStartCalibration"));
        else if (runtimeId.Length == 0 || session.State != "Connected" || !FreshState()) Error(Loc.Get("WaitingForTheHeadsetCamerasMakeSureTheHeadset"));
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
            else if (action != "heartbeat") Error(Loc.Get("WaitingForThePreviousAction"));
            return false;
        }
        var data = fields ?? new JsonObject();
        (data["id"], data["runtimeId"], data["action"]) = (command + 1, runtimeId, action);
        CalibrationSettings.WriteJson(Path.Combine(session.Root, "studio.command.json"), data);
        command++;
        heartbeat = DateTime.UtcNow;
        return true;
    }

    async Task Conclude(Func<Task<string>> work, string? title = null, string? detail = null)
    {
        (training, trainingKind) = (true, kind);
        ClearNotice();
        RefreshCalibrationControls();
        ShowWorking(title, detail);
        (bool Passed, string Message) result;
        try { result = (true, await work()); }
        catch (Exception error)
        {
            result = trainingKind.StartsWith("benchmark") ? (true, Loc.Format("BenchmarkPrepareFailure", error.Message)) : (false, error.Message);
        }
        training = false;
        progress.IsIndeterminate = false;
        Finished(result.Passed, result.Message);
    }

    void ShowWorking(string? title = null, string? detail = null)
    {
        if (page != "Calibration" || !training) return;
        (poseTitle.Text, poseDetail.Text, cue.Text) = (title ?? Loc.Get("CreatingYourCalibration"), detail ?? Loc.Get("ThisTakesAFewSecondsTrackingStaysOn"), "");
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
            Error(passed ? message : Loc.Format("CalibrationNotSaved", GroupTitle(trainingKind)));
            return;
        }
        CalibrationCard.Content = CalibrationResultPanel;
        var benchmark = passed && trainingKind.StartsWith("benchmark");
        ResultTitle.Text = benchmark ? Loc.Get("BenchmarkRecorded") : passed ? Loc.Get("CalibrationIsOn") : Loc.Get("CalibrationWasnTSaved");
        ResultDetail.Text = benchmark ? message : passed ? Loc.Get("CheckYourAvatarToSeeTheDifference") : Loc.Get("YourCurrentCalibrationHasnTChanged");
        ResultFiles.Visibility = Visible(benchmark && File.Exists(candidate));
        var lines = passed ? [] : message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ResultError.Text = lines.FirstOrDefault() ?? Loc.Get("TryRecordingYourPosesAgain");
        ResultError.Visibility = Visible(!passed);
        ResultFindings.IsExpanded = false;
        ResultLines.ItemsSource = lines.Skip(1);
        ResultDetailsHost.Children.Clear();
        if (lines.Length > 1) ResultDetailsHost.Children.Add(ResultFindings);
        begin.Content = passed ? Loc.Get("CalibrateAgain") : Loc.Get("TryAgain");
        begin.Appearance = passed ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
        review.Visibility = Visible(File.Exists(prefix + ".avi"));
        UIElementAutomationPeer.FromElement(ResultTitle)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    void ShowBenchmarkFile(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{candidate}\"").Dispose(); }
        catch (Exception) { Error(Loc.Format("ExplorerFailure", Path.GetDirectoryName(candidate))); }
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
                Error(Loc.Get("CalibrationStoppedBecauseTrackingStoppedOrTheCameraFeed"));
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
            if (recording && !FreshState()) Error(Loc.Get("CameraFeedPausedMakeSureTheHeadsetIsAwake"));
            if (page == "Cameras" && IsVisible) await UpdateCameras();
            if (IsVisible) adjustmentRefresh?.Invoke();
            if (Loc.Text(state["error"]) is { Length: > 0 } error && (recording ? Ack >= recordingCommand : page == "Calibration")) Error(error);
            if (page != "Tracking" && session.Hybrid is not null && !Session.Alive(session.Hybrid)) Error(Loc.Get("HybridHandsStoppedTheHybridHandsLogInSettings"));
        }
        catch (Exception error) { Error(error.Message); }
        finally { polling = false; }
    }

    void UpdateGuide()
    {
        if (cancelPending || !FreshState() || Ack < recordingCommand || state["kind"]?.GetValue<string>() != kind) return;
        if (state["title"] is { } title) poseTitle.Text = Loc.Text(title);
        poseDetail.Text = Loc.Text(state["instruction"]);
        cue.Text = kind == "pupils" || state["cue"]?.GetValue<string>() == "SecondsRemaining"
            ? Loc.Format("SecondsRemaining", state["remaining"]?.GetValue<double>()) : state["cue"] is { } cueText ? Loc.Text(cueText) : Loc.Get("LookAtTheCircle");
        var index = state["index"]?.GetValue<int>() ?? 0;
        progress.Value = (state["progress"]?.GetValue<double>() ?? index / 4.0) * 100;
        count.Text = Loc.Format("Of", index + 1, state["total"]?.GetValue<int>() ?? 4);
        if (kind == "pupils") SetPupilStimulus(state["level"]?.GetValue<double>() ?? 0);
        else DrawFace(state["targets"]?.AsObject() ?? new());
        pause.Content = state["paused"]?.GetValue<bool>() == true ? Loc.Get("Resume") : Loc.Get("Pause");
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
                Finished(true, Loc.Get("PupilCalibrationIsOn"));
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
                    return Loc.Get("FaceCalibrationIsOn");
                });
        }
        else if (!session.Tester)
        {
            (candidate, trainingKind) = ("", kind);
            Finished(true, Loc.Format("BenchmarkRecordedFiles", Path.GetFileName(prefix), Path.GetDirectoryName(prefix)));
        }
        else
        {
            candidate = "";
            _ = Conclude(async () =>
            {
                candidate = await session.Package(prefix);
                return Loc.Format("SendRecordingDetails", Path.GetFileName(candidate), new FileInfo(candidate).Length / 1e6);
            }, Loc.Get("PreparingYourRecording"), Loc.Get("ThisTakesAboutAMinuteTrackingStaysOn"));
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
