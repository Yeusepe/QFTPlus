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
    bool switchingBenchmark, restoreBenchmarkPending;
    bool HeadsetBenchmark => HeadsetModelExperiment.Standalone(session) && kind.StartsWith("benchmark");
    long recordingCommand;
    DateTime heartbeat = DateTime.MinValue;
    (bool Passed, string Message)? calibrationResult;
    WindowState? restoreState;
    readonly Button repeatPose = new();
    readonly Button restoreTracking = new();
    ComboBox? pupilSourcePicker;
    bool Unlabeled => kind == "benchmark-unlabeled";
    bool InputReady => kind == "pupils" || FreshState() && state[Unlabeled ? "cameraReady" : "faceReady"]?.GetValue<bool>() == true;
    string InputIssue => Unlabeled ? "CalibrationCameraDataUnavailable" : state["faceIssue"]?.GetValue<string>() is { Length: > 0 } issue ? issue : "CalibrationFaceDataUnavailable";
    int PupilSource => session.Config["pupilCalibrationSource"]?.GetValue<int>() is 1 ? 1 : session.Config["pupilCalibrationSource"]?.GetValue<int>() is 2 ? 2 : 3;

    Ui.CardExpander PupilSourceChoice()
    {
        var panel = new StackPanel();
        var picker = new ComboBox { ItemsSource = new[] { Loc.Get("CalibrationBothEyes"), Loc.Get("CalibrationLeftEye"), Loc.Get("CalibrationRightEye") },
            SelectedIndex = PupilSource == 1 ? 1 : PupilSource == 2 ? 2 : 0, IsEnabled = !recording && !training,
            MinWidth = 200, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0) };
        pupilSourcePicker = picker;
        var label = Text(Loc.Get("CalibrationPupilSource"));
        label.Margin = new(0, 0, 16, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetLabeledBy(picker, label);
        AutomationProperties.SetName(picker, Loc.Get("CalibrationPupilSource"));
        AutomationProperties.SetHelpText(picker, Loc.Get("CalibrationPupilSourceAbout"));
        picker.ToolTip = Loc.Get("CalibrationPupilSourceAbout");
        var row = new DockPanel { Margin = new(0, 0, 0, 8) };
        DockPanel.SetDock(picker, Dock.Right);
        row.Children.Add(picker);
        row.Children.Add(label);
        var about = Muted("");
        about.Margin = new(0);
        void Explain() => about.Text = Loc.Get(picker.SelectedIndex == 0 ? "CalibrationPupilSourceChooseHelp" : "CalibrationPupilSourceMirrorHelp");
        picker.SelectionChanged += (_, _) =>
        {
            session.Save("pupilCalibrationSource", picker.SelectedIndex == 1 ? 1 : picker.SelectedIndex == 2 ? 2 : 3);
            Explain();
        };
        Explain();
        panel.Children.Add(row);
        panel.Children.Add(about);
        return new Ui.CardExpander { Header = Loc.Get("Advanced"), Content = panel, Margin = new(0, 0, 0, 16) };
    }
    long Ack => state["ack"]?.GetValue<long>() ?? 0;
    DateTime lastFresh = DateTime.UtcNow;
    bool FreshState() => state["time"]?.GetValue<double>() is { } time && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - time) < 3;
    static string GroupTitle(string kind) => kind == "pupils" ? Loc.Get("Pupils") : kind.StartsWith("benchmark") ? Loc.Get("Benchmark") : Loc.Get("Face");

    void DrawFace(JsonObject targets)
    {
        face.Configure(preview ? null : session);
        face.Show(targets, state["condition"]?.GetValue<string>() ?? "", kind, !preview, recording ? state["index"]?.GetValue<int>() ?? -1 : -1, state["count"]?.GetValue<int>() ?? 0);
    }

    void ResizeGuide()
    {
        if (page != "Calibration") return;
        face.Height = Math.Clamp(ActualHeight - 550, 100, 230);
        DrawFace(recording ? state["targets"]?.AsObject() ?? new() : new());
    }

    void RefreshCalibrationControls()
    {
        foreach (ListBoxItem item in Navigation.Items) item.IsEnabled = !recording && !switchingBenchmark;
        calibrationModes.IsEnabled = !recording && !training && !switchingBenchmark;
        if (pupilSourcePicker is not null) pupilSourcePicker.IsEnabled = !recording && !training;
        slowMode.Visibility = Visible(kind == "enroll" && !recording && !training);
        begin.Visibility = Visible(!recording && !training);
        begin.IsEnabled = !switchingBenchmark && (HeadsetBenchmark || InputReady);
        if (kind != "pupils" && InputReady &&
            (Notice.Text == Loc.Get("CalibrationFacePortConflict") || Notice.Text == Loc.Get("CalibrationFaceDataUnavailable") ||
             Notice.Text == Loc.Get("CalibrationCameraDataUnavailable"))) ClearNotice();
        if (!recording && !training && !InputReady && !HeadsetBenchmark)
        {
            count.Text = Loc.Get(InputIssue);
            count.Visibility = Visibility.Visible;
        }
        else if (!recording && !training && kind != "pupils") count.Text = Loc.Get("RecordingsStayOnThisPC");
        setUpFace.Visibility = Visible(SetupFirst && !recording && !training);
        pause.Visibility = Visible(recording && !Unlabeled);
        skip.Visibility = repeatPose.Visibility = Visible(recording && kind != "pupils" && !Unlabeled);
        cancel.Visibility = Visible(recording);
        restoreTracking.Visibility = Visible(session.BenchmarkSwitch && !recording && !training && !switchingBenchmark);
        progress.Visibility = Visible(recording && !Unlabeled || training);
        StartButton.IsEnabled = !recording && !switchingBenchmark;
    }

    void Calibration()
    {
        if (HeadsetModelExperiment.Standalone(session) && !kind.StartsWith("benchmark"))
        {
            Subtitle.Text = Loc.Get("RunsInQFTHeadsetWhereAnAvatarShowsEach");
            Subtitle.Visibility = Visibility.Visible;
            var status = Muted("", live: true);
            var list = new StackPanel();
            var rows = new List<(TextBlock Value, string Kind, string About)>();
            var pupilOptions = PupilSourceChoice();
            var watching = false;
            foreach (var (kind, title, icon, about, primary) in new[] {
                         ("face", Loc.Get("FaceAndTongue"), "sentiment_satisfied", Loc.Get("Copy12ExpressionsFromAMetaAvatarAbout75"), true),
                         ("pupils", Loc.Get("PupilDilation"), "adjust", Loc.Get("FollowADotAsTheSpaceDarkensAndBrightens"), false) })
            {
                var start = AsyncButton(Loc.Get("Calibrate_9604ba0c"), async () =>
                {
                    try
                    {
                        session.App = await session.SendApp(CancellationToken.None, ("calibrate", kind), ("pupilSource", PupilSource));
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
                pupilOptions.IsEnabled = session.App?["calibration"]?["running"]?.GetValue<bool>() != true;
                foreach (var (value, kind, about) in rows)
                    value.Text = session.App is not { } app ? about : Loc.Format("CalibrationStatusDetails",
                        Loc.Get(kind == "face" && HeadsetApp.Flag(app,"calibrated","facePartial") ? "CalibrationPartiallyCalibrated" : HeadsetApp.Flag(app, "calibrated", kind) ? "Calibrated" : "NotCalibrated"), about);
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
            foreach (var element in new UIElement[] { list, note, status, pupilOptions }) Page.Children.Add(element);
            return;
        }
        var benchmarksAvailable = session.Tester || session.Config["benchmarkMode"]?.GetValue<bool>() == true;
        if (training) kind = trainingKind;
        else if (!benchmarksAvailable && kind.StartsWith("benchmark")) kind = "enroll";
        calibrationModes.SelectionChanged -= CalibrationModeChanged;
        calibrationModes.Items.Clear();
        foreach (var (id, title) in (HeadsetBenchmark || session.BenchmarkSwitch ? Array.Empty<(string, string)>() : new[] { ("enroll", Loc.Get("Face")), ("pupils", Loc.Get("Pupils")) })
                     .Concat(benchmarksAvailable ? [("benchmark-quick", Loc.Get("ShortBenchmark")), ("benchmark", Loc.Get("FullBenchmark")), ("benchmark-unlabeled", Loc.Get("UnlabeledBenchmark"))] : []))
            calibrationModes.Items.Add(new ComboBoxItem { Content = title, Tag = id, IsSelected = kind == id });
        calibrationModes.SelectionChanged += CalibrationModeChanged;
        var benchmark = kind.StartsWith("benchmark");
        poseTitle.Text = benchmark ? ((ComboBoxItem)calibrationModes.SelectedItem).Content.ToString() : Loc.Format("CalibrateGroup", GroupTitle(kind));
        var instructions = kind switch { "enroll" => "Face", "benchmark-quick" => "QuickBenchmark", "benchmark" => "Benchmark", _ => "Pupil" };
        poseDetail.Text = Loc.Get(Unlabeled ? "UnlabeledBenchmarkInstructions" : instructions + "CalibrationInstructions" + (SetupFirst ? "NeedsSetup" : ""));
        if (HeadsetBenchmark) poseDetail.Text = Loc.Get("BenchmarkTemporaryPC");
        cue.Text = kind switch { "pupils" => Loc.Get("About80Seconds"), "benchmark" => Loc.Get("About18Minutes"), "benchmark-quick" => Loc.Get("About5Minutes"), "benchmark-unlabeled" => "", _ => EnrollLength };
        face.Visibility = Visible(!Unlabeled);
        CalibrationCard.Content = CalibrationGuide;
        (progress.IsIndeterminate, progress.Value) = (false, 0);
        count.Text = kind == "pupils" ? "" : Loc.Get("RecordingsStayOnThisPC");
        count.Visibility = Visible(kind != "pupils");
        slowMode.IsChecked = slow;
        begin.Content = Loc.Get(SetupFirst ? "RecordWithoutFaceSetup" : benchmark ? "RecordABenchmark" : "StartCalibration");
        begin.Appearance = SetupFirst ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
        pause.Content = Loc.Get("Pause");
        cancel.IsEnabled = true;
        cancel.Content = Loc.Get(Unlabeled ? "StopAndSaveRecording" : "Cancel");
        review.Visibility = Visibility.Collapsed;
        foreach (var element in new UIElement[] { CalibrationAreaLabel, calibrationModes }) Page.Children.Add(element);
        foreach (var element in new UIElement[] { CalibrationCard, slowMode }) Page.Children.Add(element);
        if (kind == "pupils") Page.Children.Add(PupilSourceChoice());
        repeatPose.Content = Loc.Get("CalibrationRepeatPose"); repeatPose.Tag = "repeat";
        repeatPose.Click -= GuideCommand; repeatPose.Click += GuideCommand;
        Actions.Children.Clear();
        restoreTracking.Content = Loc.Get("BenchmarkRestoreTracking");
        restoreTracking.Click -= RestoreTrackingClick;
        restoreTracking.Click += RestoreTrackingClick;
        repeatPose.Margin = new(8, 0, 0, 0);
        foreach (var button in new[] { setUpFace, begin, pause, skip, repeatPose, cancel, review, restoreTracking }) Actions.Children.Add(button);
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

    bool SetupFirst => !HeadsetBenchmark && !session.BenchmarkSwitch && kind is ("benchmark" or "benchmark-quick") && !File.Exists(session.Config["faceEnrollment"]?.GetValue<string>());

    void SetUpFaceFirst(object sender, RoutedEventArgs e)
    {
        if (recording || training) return;
        (kind, candidate, prefix, calibrationResult) = ("enroll", "", "", null);
        CalibrationReset();
    }

    string EnrollLength => slow ? Loc.Get("CalibrationFaceDurationSlow") : Loc.Get("CalibrationFaceDuration");
    async void BeginCalibration(object sender, RoutedEventArgs e)
    {
        if (switchingBenchmark || recording || training) return;
        if (!HeadsetBenchmark) { Begin(); return; }
        if (busy) return;
        switchingBenchmark = busy = true;
        cue.Text = Loc.Get("BenchmarkPreparing");
        RefreshCalibrationControls();
        try
        {
            await session.PrepareBenchmarkTracking();
            state = new();
            runtimeId = "";
            command = 0;
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!FreshState() || session.State != "Connected" || !InputReady)
            {
                session.Poll();
                state = Session.Read(Path.Combine(session.Root, "studio.state.json"));
                runtimeId = state["runtimeId"]?.GetValue<string>() ?? "";
                command = Ack;
                if (!session.Running || session.State == "TrackingStopped") throw new IOException(session.Detail);
                if (DateTime.UtcNow >= deadline) throw new IOException(Loc.Get("BenchmarkCameraTimeout"));
                await Task.Delay(250);
            }
            Begin();
            if (!recording) throw new IOException(Loc.Get("BenchmarkCameraTimeout"));
        }
        catch (Exception error)
        {
            Error(error.Message);
            restoreBenchmarkPending = session.BenchmarkSwitch;
        }
        finally
        {
            switchingBenchmark = busy = false;
            RefreshCalibrationControls();
        }
    }
    void GuideCommand(object sender, RoutedEventArgs e)
    {
        var action = (string)((Button)sender).Tag;
        Send(action, action is "skip" or "repeat" ? new JsonObject { ["index"] = state["index"]?.DeepClone() } : null);
    }
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
        cue.Text = Loc.Get(Unlabeled ? "SavingRecording" : "StoppingCalibration");
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
        else if (!InputReady) Error(Loc.Get(InputIssue));
        else
        {
            ClearNotice();
            (candidate, prefix) = ("", "");
            if (!Send("begin", new JsonObject { ["kind"] = kind, ["slow"] = slow,
                    ["sourceEyes"] = PupilSource == 1 ? new JsonArray(0) : PupilSource == 2 ? new JsonArray(1) : new JsonArray(0, 1) })) return;
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

    void RestoreTrackingClick(object sender, RoutedEventArgs e)
    {
        if (!recording && !training && !switchingBenchmark) restoreBenchmarkPending = true;
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
        if (page == "Settings") trackingRefresh?.Invoke();
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
        if (session.BenchmarkSwitch && trainingKind.StartsWith("benchmark")) restoreBenchmarkPending = true;
        calibrationResult = (passed, message);
        if (page != "Calibration" || kind != trainingKind)
        {
            RefreshCalibrationControls();
            Error(passed ? message : Loc.Format("CalibrationNotSaved", GroupTitle(trainingKind)));
            return;
        }
        CalibrationCard.Content = CalibrationResultPanel;
        var benchmark = passed && trainingKind.StartsWith("benchmark");
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var partial = passed && trainingKind == "enroll" && lines.Length > 1;
        ResultTitle.Text = benchmark ? Loc.Get("BenchmarkRecorded") : partial ? Loc.Get("CalibrationPartiallyCalibrated") : passed ? Loc.Get("CalibrationIsOn") : Loc.Get("CalibrationWasnTSaved");
        ResultDetail.Text = passed ? lines.FirstOrDefault() ?? message : Loc.Get("YourCurrentCalibrationHasnTChanged");
        ResultFiles.Visibility = Visible(benchmark && File.Exists(candidate));
        ResultError.Text = lines.FirstOrDefault() ?? Loc.Get("TryRecordingYourPosesAgain");
        ResultError.Visibility = Visible(!passed);
        ResultFindings.IsExpanded = false;
        ResultLines.ItemsSource = lines.Skip(1);
        ResultDetailsHost.Children.Clear();
        if (lines.Length > 1 && (!passed || partial)) ResultDetailsHost.Children.Add(ResultFindings);
        begin.Content = Loc.Get(benchmark ? "RecordAgain" : passed && !partial ? "CalibrateAgain" : "TryAgain");
        begin.Appearance = passed && !partial ? Wpf.Ui.Controls.ControlAppearance.Secondary : Wpf.Ui.Controls.ControlAppearance.Primary;
        RefreshCalibrationControls();
        review.Visibility = Visible(File.Exists(prefix + ".avi"));
        UIElementAutomationPeer.FromElement(ResultTitle)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    void ShowBenchmarkFile(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{candidate}\"").Dispose(); }
        catch (Exception) { Error(Loc.Format("ExplorerFailure", Path.GetDirectoryName(candidate))); }
    }

    internal static string FaceCalibrationMessage(JsonObject capture, JsonObject report)
    {
        if (capture["requiresValidation"]?.GetValue<bool>() != true || capture["steps"] is not JsonArray entries)
            return Loc.Get("FaceCalibrationIsOn");
        var steps = entries.OfType<JsonObject>().ToArray();
        var validated = report["validatedSlots"]?.AsArray().Select(n => n?.GetValue<string>()).ToHashSet() ?? [];
        var findings = new List<string>();
        foreach (var group in steps.Where(s => s["condition"]?.GetValue<string>()?.StartsWith("anchor:") == true)
                     .GroupBy(s => s["condition"]!.GetValue<string>()[7..]))
        {
            if (validated.Contains(group.Key)) continue;
            var attempt = group.Any(s => s["gate"]?["passed"]?.GetValue<bool>() == true)
                ? steps.LastOrDefault(s => s["condition"]?.GetValue<string>() == "validate:" + group.Key) : group.Last();
            var reason = attempt?["skipped"]?.GetValue<bool>() == true ? Loc.Get("CalibrationPoseSkipped")
                : attempt?["gate"]?["passed"]?.GetValue<bool>() == true ? Loc.Get("GuideNeedMoreCalibrationImages")
                : attempt?["gate"]?["reason"] is { } failure && failure.GetValue<string>() != "skipped" ? Loc.Text(failure)
                : Loc.Get("CalibrationPoseNotValidated");
            findings.Add(Loc.Format("CalibrationPoseFailure", Loc.Text(group.First()["name"]), reason));
        }
        if (report["features"]?["tongueDirection"]?.GetValue<bool>() == false)
        {
            var complete = new[] { "tongue_out", "tongue_up", "tongue_down", "tongue_left", "tongue_right" }.All(validated.Contains);
            findings.Add(Loc.Format("CalibrationPoseFailure", Loc.Get("CalibrationTongueDirection"),
                Loc.Get(complete ? "CalibrationTongueDirectionsNotDistinct" : "CalibrationTonguePosesMissing")));
        }
        if (report["features"]?["cheekSeparation"]?.GetValue<bool>() == false)
            findings.Add(Loc.Format("CalibrationPoseFailure", Loc.Get("CalibrationCheekSeparation"), Loc.Get("CalibrationCheeksNotDistinct")));
        return findings.Count == 0 ? Loc.Get("FaceCalibrationIsOn")
            : string.Join('\n', new[] { Loc.Get("CalibrationPartialSummary") }.Concat(findings));
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
            if (switchingBenchmark) return;
            if (!busy) session.Poll();
            if (!preview) session.GameVideo.Poll();
            try { state = Session.Read(Path.Combine(session.Root, "studio.state.json")); }
            catch (InvalidDataException) { state = new(); }
            var id = state["runtimeId"]?.GetValue<string>() ?? "";
            var restarted = id != runtimeId;
            if (restarted) (runtimeId, command) = (id, Ack);
            if (FreshState() || !recording) lastFresh = DateTime.UtcNow;
            if (recording && (!session.Running || restarted || DateTime.UtcNow - lastFresh > TimeSpan.FromSeconds(15)))
            {
                restoreBenchmarkPending = session.BenchmarkSwitch;
                (recording, cancelPending) = (false, false);
                Theme("System");
                if (restoreState is { } before) (WindowState, restoreState) = (before, null);
                if (page == "Calibration") CalibrationReset(); else RefreshCalibrationControls();
                Error(Loc.Get("CalibrationStoppedBecauseTrackingStoppedOrTheCameraFeed"));
            }
            var settled = FreshState() && Ack >= command;
            if (cancelPending && settled && Send(Unlabeled ? "finish" : "cancel"))
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
            if (page == "Calibration") RefreshCalibrationControls();
            if (recording && !FreshState()) Error(Loc.Get("CameraFeedPausedMakeSureTheHeadsetIsAwake"));
            if (page == "Cameras" && IsVisible) await UpdateCameras();
            if (IsVisible) adjustmentRefresh?.Invoke();
            if (Loc.Text(state["error"]) is { Length: > 0 } error && (recording ? Ack >= recordingCommand : page == "Calibration")) Error(error);
            if (page != "Tracking" && session.Hybrid is not null && !Session.Alive(session.Hybrid)) Error(Loc.Get("HybridHandsStoppedTheHybridHandsLogInSettings"));
            if (restoreBenchmarkPending && !switchingBenchmark && !recording && !training)
            {
                restoreBenchmarkPending = false;
                switchingBenchmark = busy = true;
                if (page == "Calibration" && calibrationResult is null) cue.Text = Loc.Get("BenchmarkRestoring");
                RefreshCalibrationControls();
                try { await session.RestoreBenchmarkTracking(); }
                finally
                {
                    switchingBenchmark = busy = false;
                    UseChanged(session.Use);
                    RefreshCalibrationControls();
                }
            }
        }
        catch (Exception error) { Error(error.Message); }
        finally { polling = false; }
    }

    void UpdatePose()
    {
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
        repeatPose.IsEnabled = state["waitingForFace"]?.GetValue<bool>() != true;
        skip.IsEnabled = repeatPose.IsEnabled && (kind != "enroll" || state["condition"]?.GetValue<string>() is string condition && condition is not ("neutral" or "validate:neutral"));
    }

    void UpdateGuide()
    {
        if (cancelPending || !FreshState() || Ack < recordingCommand || state["kind"]?.GetValue<string>() != kind) return;
        if (Unlabeled)
        {
            cue.Text = Loc.Format("BenchmarkElapsed", TimeSpan.FromSeconds(state["elapsed"]?.GetValue<long>() ?? 0));
            count.Text = Loc.Get(state["cameraReady"]?.GetValue<bool>() == true ? "RecordingsStayOnThisPC" : "CalibrationCameraDataUnavailable");
        }
        else UpdatePose();
        if (state["phase"]?.GetValue<string>() is not ("complete" or "cancelled")) return;
        recording = false;
        Theme("System");
        if (restoreState is { } before) (WindowState, restoreState) = (before, null);
        if (state["phase"]!.GetValue<string>() != "complete")
        {
            restoreBenchmarkPending = session.BenchmarkSwitch;
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
                    var capture = Session.Read(prefix + ".qpsession.json");
                    var message = FaceCalibrationMessage(capture, Session.Read(prefix + ".calibration.json"));
                    Apply("enroll");
                    return message;
                });
        }
        else if (Unlabeled)
        {
            (candidate, trainingKind) = (prefix + ".qpcap", kind);
            Finished(true, Loc.Format("BenchmarkRecordedFiles", Path.GetFileName(prefix), Path.GetDirectoryName(prefix)));
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
