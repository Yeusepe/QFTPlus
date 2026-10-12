using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Qpro.GazeBridge;
using Ui = Wpf.Ui.Controls;

namespace QFTPlus;

public partial class StudioWindow
{
    static readonly (int From, string Title, string About)[] SetupSteps =
    [
        (0, Loc.Get("FindYourQuestPro"), Loc.Get("LooksForYourHeadsetOverUSBAndWiFi")),
        (10, Loc.Get("AllowAccess"), Loc.Get("ApproveUSBDebuggingInTheHeadsetThenAllowShell")),
        (25, Loc.Get("TurnOnWiFi"), Loc.Get("SwitchesTheHeadsetToWiFiSoYouCan")),
        (35, Loc.Get("InstallComponents"), Loc.Get("OneTimeInstallOfTheTrackingRuntimeOnThis")),
        (55, Loc.Get("PrepareEyeTracking"), Loc.Get("BuildsTheEyeModelFromYourHeadsetKeepThe")),
        (60, Loc.Get("CheckVRApps"), Loc.Get("MakesSureSteamSteamVRAndVRCFaceTrackingAreInstalled")),
        (70, Loc.Get("UpdateVRCFaceTracking"), Loc.Get("InstallsTheQFTModuleVRCFaceTrackingClosesBrieflyIfIt"))
    ];
    static readonly (string Id, string Title, string About)[] Uses =
    [
        ("face", Loc.Get("FaceAndEyeTracking"), Loc.Get("SendsYourExpressionsAndEyeMovementToVRCFaceTracking")),
        ("hands", Loc.Get("HybridHandsAndControllers"), Loc.Get("HandTrackingAndControllersTogetherInVirtualDesktopDoesn")),
        ("both", Loc.Get("Both"), Loc.Get("FaceAndEyeTrackingPlusHybridHandsWheneverYou"))
    ];
    bool manualTesting, searching;
    long manualPublished;
    JsonObject manualValues = new();
    internal string outputParameter = "*", manualGroup = "Tongue";
    internal Action? setupRefresh, adjustmentRefresh;
    internal int setupStep = -1;
    internal bool setupDone, moduleMissing;
    internal string setupProblem = "";
    List<SetupService.Headset> headsets = [];
    DateTime? headsetsChecked;
    sealed record ChoiceContent(string Title, string About);

    string[] Parameters() => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(session.Root, "tracking-parameters.json"))) ?? [];
    static string Label(string key) => Regex.Replace(key, "([a-z])([A-Z])", "$1 $2");
    static string AreaTitle(string area) => Loc.Get(area == "Throat and neck" ? "ThroatAndNeck" : area);
    static double Neutral(string name) => name.StartsWith("Pupil") ? .5 : name.StartsWith("Openness") ? 1 : 0;
    static string? Pair(string name) => name.StartsWith("Gaze") ? Regex.Replace(name, "Left|Right", "")
        : OutputAdjustments.Partner(name) is null ? null : Regex.Replace(name, "(Left|Right)$", "");
    static string Both(string pair) => Label(pair) + Loc.Get("BothSides");

    void Field(Panel parent, string title, Control control)
    {
        var label = Text(title);
        AutomationProperties.SetLabeledBy(control, label);
        AutomationProperties.SetName(control, title);
        parent.Children.Add(label);
        parent.Children.Add(control);
    }

    RadioButton Choice(string group, string title, string about, bool selected, Action picked)
    {
        var radio = new RadioButton { GroupName = group, Content = new ChoiceContent(title, about), IsChecked = selected, Style = (Style)FindResource("Choice") };
        radio.Checked += (_, _) => picked();
        return radio;
    }

    ComboBox Picker(object[] items, object? selected, Action<int> choose, Func<ComboBox> refocus)
    {
        var picker = new ComboBox { ItemsSource = items, SelectedItem = selected };
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0) return;
            var focus = picker.IsKeyboardFocusWithin;
            choose(picker.SelectedIndex);
            if (focus) refocus().Focus();
        };
        return picker;
    }

    void Adjustments()
    {
        var parameters = Parameters();
        string[] Members(string area) => parameters.Where(p => OutputAdjustments.Area(p) == area).ToArray();
        var areas = OutputAdjustments.Areas.Where(a => Members(a).Length > 0).ToArray();
        var area = outputParameter == "*" ? null : OutputAdjustments.Areas.Contains(outputParameter) ? outputParameter : OutputAdjustments.Area(outputParameter);
        var single = area is not null && area != outputParameter;
        var members = area is null ? parameters : Members(area);
        string[] Sides(string pair) => parameters.Where(p => Pair(p) == pair).ToArray();
        var sides = Sides(outputParameter);
        string[] targets = sides.Length > 0 ? sides : [outputParameter];
        string[] names = single ? targets : members;
        void Choose(string choice)
        {
            outputParameter = choice;
            Navigate("Adjustments");
        }
        var areaPicker = Picker([Loc.Get("AllAreas"), .. areas.Select(AreaTitle)], area is null ? Loc.Get("AllAreas") : AreaTitle(area), i => Choose(i == 0 ? "*" : areas[i - 1]), () => Page.Children.OfType<ComboBox>().First());
        areaPicker.Margin = new(0, 0, 0, area is null ? 24 : 16);
        Field(Page, Loc.Get("Area"), areaPicker);
        if (area is not null)
        {
            var choices = members.SelectMany(m => Pair(m) is { } pair && Sides(pair) is [var first, _] && first == m ? new[] { pair, m } : [m]).ToArray();
            var parameterPicker = Picker([Loc.Get("AllParameters"), .. choices.Select(c => Sides(c).Length > 0 ? Both(c) : Label(c))],
                !single ? Loc.Get("AllParameters") : sides.Length > 0 ? Both(outputParameter) : Label(outputParameter),
                i => Choose(i == 0 ? area : choices[i - 1]), () => Page.Children.OfType<ComboBox>().ElementAt(1));
            parameterPicker.Margin = new(0, 0, 0, 24);
            Field(Page, Loc.Get("Parameter"), parameterPicker);
        }
        if (area == "Gaze" && !single) Page.Children.Add(ConvergenceOptions());
        var path = Path.Combine(session.Root, "output-settings.json");
        JsonObject? Load()
        {
            try { return Session.Read(path); }
            catch (InvalidDataException error)
            {
                Error(error.Message);
                return null;
            }
        }
        if (Load() is not { } config) return;
        var inherited = config["*"] as JsonObject ?? new();
        var group = single ? config[area!] as JsonObject ?? new() : new JsonObject();
        var values = (config[targets[0]] as JsonObject ?? new()).DeepClone().AsObject();
        double Value(string key, double fallback, bool inherit = true) =>
            OutputAdjustments.Number(values, key, OutputAdjustments.Number(group, key, inherit ? OutputAdjustments.Number(inherited, key, fallback) : fallback));
        var gaze = area == "Gaze";
        var (minimum, maximum, scale, format, unit, decimals) = gaze ? (-1.2, 1.2, 1.0, "0.###", "", 3) : (0.0, 1.0, 100.0, "0.##'%'", "%", 2);
        var status = Muted("", live: true);
        var readout = Text("", 16);
        AutomationProperties.SetName(readout, Loc.Get("LiveAdjustmentValues"));
        var statusCard = area is null || single ? Card(status, readout) : Card(status);
        Page.Children.Add(statusCard);
        if (area is null) readout.Visibility = Visibility.Collapsed;
        if (!single) status.Margin = new(0);
        if (area is not null && !single) Page.Children.Add(new Ui.CardExpander { Header = Loc.Get("LiveValues"), Content = readout, Margin = new(0, -8, 0, 16) });
        var problem = Text("", 13, live: true);
        problem.Visibility = Visibility.Collapsed;
        Page.Children.Add(problem);
        JsonObject edited = new();
        var changed = new HashSet<string>();
        void Save(string key, double value)
        {
            values[key] = value;
            changed.Add(key);
            var valid = Value("inputMax", maximum, false) - Value("inputMin", minimum, false) >= .0001 && Value("outputMin", minimum, false) <= Value("outputMax", maximum, false);
            problem.Text = valid ? "" : Loc.Get("InputMinimumMustBeBelowInputMaximumOutputMinimum");
            problem.Visibility = Visible(!valid);
            if (!valid) return;
            edited = new(changed.Select(k => KeyValuePair.Create(k, values[k]?.DeepClone())));
            SaveLater(Write);
        }
        void Write()
        {
            if (Load() is not { } saved) return;
            foreach (var target in targets)
            {
                var own = saved[target] as JsonObject ?? new();
                foreach (var (key, value) in edited) own[key] = value?.DeepClone();
                saved[target] = own;
            }
            CalibrationSettings.WriteJson(path, saved);
            adjustmentRefresh?.Invoke();
        }
        Slider Number(Panel panel, string title, string key, double min, double max, double fallback, bool inherit = true, bool raw = false) =>
            NumericSetting.AddTo(panel, title, min, max, Value(key, fallback, inherit), v => Save(key, v), raw ? "" : unit, raw ? 1 : scale, raw ? 2 : decimals);
        CheckBox Toggle(string key, string label)
        {
            var box = new CheckBox { Content = label, IsChecked = Value(key, 0) >= .5 };
            box.Click += (_, _) => Save(key, box.IsChecked == true ? 1 : 0);
            return box;
        }
        var modeled = names.Any(OutputAdjustments.Modeled);
        var scope = area is null ? Loc.Get("TheseSettingsApplyToEveryParameterUnlessAnArea")
            : single ? Loc.Format("OnlySettingsYouChangeHereOverrideAndAllAreas", area)
            : Loc.Format("TheseSettingsApplyToEveryParameterInOnlySettings", area);
        Page.Children.Add(Muted(modeled ? scope : scope + (single ? Loc.Get("QFTSCameraModelsDonTTrackThisParameter")
            : Loc.Get("QFTSCameraModelsDonTTrackTheseParameters"))));
        if (sides.Length > 1 && !JsonNode.DeepEquals(config[sides[0]], config[sides[1]]))
            Page.Children.Add(Muted(Loc.Format("TheTwoSidesHaveDifferentSettingsValuesShownAre", Label(sides[0]))));
        var paired = (!single || sides.Length > 0) && names.Any(n => OutputAdjustments.Partner(n) is not null);
        if (modeled || paired)
        {
            var input = new StackPanel();
            if (modeled)
            {
                var about = Loc.Get("SendsTheHeadsetSOwnTrackingInsteadOfQFT")
                    + (area is null or "Pupils" ? Loc.Get("TheHeadsetDoesnTMeasurePupilsSoTheyStay") : "");
                var passthrough = Toggle("passthrough", Loc.Get("HeadsetPassthrough"));
                AutomationProperties.SetHelpText(passthrough, about);
                passthrough.Margin = new(0, 4, 0, paired ? 20 : 4);
                input.Children.Add(passthrough);
            }
            if (paired)
            {
                var match = new ComboBox { ItemsSource = new[] { Loc.Get("Off"), Loc.Get("AverageBothSides"), Loc.Get("FollowTheStrongerSide") },
                    SelectedIndex = (int)Math.Clamp(Math.Round(Value("match", 0)), 0, 2), Margin = new(0, 0, 0, 8) };
                match.SelectionChanged += (_, _) => Save("match", match.SelectedIndex);
                Field(input, Loc.Get("MatchLeftAndRight"), match);
                input.Children.Add(Muted(Loc.Get("MovesTheTwoSidesOfEachPairTogetherAveraging")));
            }
            Page.Children.Add(Card(input));
        }
        var basics = new StackPanel();
        NumericSetting.AddTo(basics, Loc.Get("Strength"), 0, 10, Value("strength", 1), v => Save("strength", v), scale: 100);
        Number(basics, Loc.Get("Offset"), "offset", minimum - maximum, maximum - minimum, 0);
        Number(basics, Loc.Get("DeadZone"), "deadzone", 0, maximum - minimum, 0);
        basics.Children.Add(Muted(Loc.Get("DeadZoneIgnoresMovementAroundNeutralThenScalesThe")));
        Slider? release = null;
        var syncing = false;
        var smoothing = names.Select(n => OutputAdjustments.DefaultSmoothing(OutputAdjustments.Area(n))).Distinct().Count() == 1
            ? OutputAdjustments.DefaultSmoothing(OutputAdjustments.Area(names[0])) : 0;
        NumericSetting.AddTo(basics, Loc.Get("Smoothing"), 0, 100, Value("smoothing", smoothing), v =>
        {
            Save("smoothing", v);
            if (release is null || values.ContainsKey("release") || group.ContainsKey("release") || inherited.ContainsKey("release")) return;
            syncing = true;
            release.Value = v;
            syncing = false;
        });
        Page.Children.Add(Card(basics));
        var response = new StackPanel { Margin = new(0, 16, 0, 0) };
        Number(response, Loc.Get("ResponseCurve"), "curve", .1, 5, 1, raw: true);
        response.Children.Add(Muted(Loc.Get("Text1IsLinearBelow1BoostsSmallMovementsAbove")));
        release = NumericSetting.AddTo(response, Loc.Get("SmoothingWhenRelaxing"), 0, 100, Value("release", Value("smoothing", smoothing)), v =>
        {
            if (!syncing) Save("release", v);
        });
        response.Children.Add(Muted(Loc.Get("UsedWhileAnExpressionReturnsToRestHigherValues")));
        response.Children.Add(Toggle("invert", Loc.Get("InvertOutput")));
        Page.Children.Add(new Ui.CardExpander { Header = Loc.Get("Response"), Content = response, Margin = new(0, 0, 0, 16) });
        if (area is not null)
        {
            var limits = new StackPanel { Margin = new(0, 16, 0, 0) };
            limits.Children.Add(Muted(gaze ? Loc.Get("GazeValuesUseRadiansSetTheMovementYouCan")
                : Loc.Get("SetTheInputRangeYouCanComfortablyReachOutput")));
            Number(limits, Loc.Get("InputMinimum"), "inputMin", minimum, maximum, minimum, false);
            Number(limits, Loc.Get("InputMaximum"), "inputMax", minimum, maximum, maximum, false);
            if (names.Select(Neutral).Distinct().ToArray() is [var rest])
            {
                Number(limits, Loc.Get("InputNeutral"), "neutral", minimum, maximum, rest, false);
                limits.Children.Add(Muted(Loc.Get("NeutralIsYourRestingInputClampedToTheInput")));
            }
            Number(limits, Loc.Get("OutputMinimum"), "outputMin", minimum, maximum, minimum, false);
            Number(limits, Loc.Get("OutputMaximum"), "outputMax", minimum, maximum, maximum, false);
            Page.Children.Add(new Ui.CardExpander { Header = Loc.Get("InputAndOutputLimits"), Content = limits, IsExpanded = single, Margin = new(0, 0, 0, 16) });
        }
        Page.Children.Add(Muted(Loc.Get("LiveValuesShowTheQFTModuleSOutputYour")));
        string[] own = single ? [] : members.Where(m => config[m] is JsonObject { Count: > 0 }).ToArray();
        if (own.Length > 0)
            Page.Children.Add(Muted((own.Length == 1 ? Loc.Format("HasItsOwnSettingsWhichTakePrecedenceHere", Label(own[0])) : Loc.Format("ParametersHaveTheirOwnSettingsWhichTakePrecedenceHere", own.Length))
                + Loc.Get("SelectOneAndChooseUseInheritedSettingsToRemove")));
        adjustmentRefresh = () =>
        {
            try { File.WriteAllBytes(Path.Combine(session.Root, "output-status.lease"), []); }
            catch (IOException) { }
            JsonObject data;
            try { data = Session.Read(Path.Combine(session.Root, "output-status.json")); }
            catch (InvalidDataException) { data = new(); }
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - OutputAdjustments.Number(data, "updated", 0);
            void Show(string text)
            {
                status.Text = text;
                status.Visibility = Visible(text.Length > 0);
                statusCard.Visibility = Visible(text.Length > 0 || single);
            }
            if (age < 0 || age > 2)
            {
                readout.Text = Loc.Get("WaitingForLiveValues");
                Show(Loc.Get("NoLiveResponseFromTheQFTModuleOpenVRCFaceTracking"));
                return;
            }
            var pending = JsonNode.DeepEquals(data["settings"], Load()) ? "" : Loc.Get("ChangesSavedWaitingForTheModuleToApplyThem");
            if (area is null)
            {
                Show(pending);
                return;
            }
            var inputs = data["inputs"] as JsonObject ?? new();
            var outputs = data["outputs"] as JsonObject ?? new();
            string Shown(JsonObject from, string name) => (OutputAdjustments.Number(from, name, 0) * scale).ToString(format);
            var missing = names.Where(name => !inputs.ContainsKey(name) || !outputs.ContainsKey(name)).ToArray();
            readout.Text = string.Join("\n", names.Except(missing).Select(name => $"{Label(name)}: {Shown(inputs, name)} → {Shown(outputs, name)}")
                .Concat(missing.Length == 0 ? [] : [Loc.Format("NotProvidedByTheQFTModuleCheckItsEye", string.Join(", ", missing.Select(Label)))]));
            var pupils = area == "Pupils" && data["pupilTracking"]?.GetValue<bool>() != true
                ? Loc.Get("PupilTrackingIsOffOrHasNoFreshData") : "";
            Show(string.Join(" ", new[] { pending, pupils }.Where(t => t.Length > 0)));
        };
        adjustmentRefresh();
        Actions.Children.Add(Button(outputParameter == "*" ? Loc.Get("ResetDefaults") : Loc.Get("UseInheritedSettings"), () =>
        {
            SaveNow();
            if (Load() is { } data && targets.Count(data.Remove) > 0) CalibrationSettings.WriteJson(path, data);
            Navigate("Adjustments");
        }));
    }

    void Manual()
    {
        var choices = OutputAdjustments.Areas.Where(area => Parameters().Any(name => OutputAdjustments.Area(name) == area)).ToArray();
        var select = Picker(choices.Select(AreaTitle).ToArray(), AreaTitle(manualGroup), i =>
        {
            StopManual();
            manualGroup = choices[i];
            Navigate("Manual");
        }, () => Page.Children.OfType<ComboBox>().First());
        select.Margin = new(0, 0, 0, 12);
        Field(Page, Loc.Get("Area"), select);
        manualValues = new();
        var sliders = new StackPanel { IsEnabled = false };
        var enabled = new CheckBox { Content = Loc.Get("OverrideLiveTracking"), IsChecked = false };
        enabled.Click += (_, _) =>
        {
            manualTesting = enabled.IsChecked == true;
            if (manualTesting && !Processes.Running("VRCFaceTracking"))
            {
                enabled.IsChecked = manualTesting = false;
                Error(Loc.Get("OpenVRCFaceTrackingToTestMovements"));
            }
            sliders.IsEnabled = manualTesting;
            PublishManual();
        };
        AutomationProperties.SetHelpText(enabled, Loc.Get("WhileItSOnVRCFaceTrackingGetsTheseValuesInstead"));
        enabled.Margin = new(0, 4, 0, 16);
        foreach (var name in Parameters().Where(name => OutputAdjustments.Area(name) == manualGroup))
        {
            manualValues[name] = Neutral(name);
            var gaze = name.StartsWith("Gaze");
            NumericSetting.AddTo(sliders, Label(name), gaze ? -1.2 : 0, gaze ? 1.2 : 1, Neutral(name), v =>
            {
                manualValues[name] = v;
                if (Environment.TickCount64 - manualPublished >= 100) PublishManual();
            }, gaze ? "" : "%", gaze ? 1 : 100, gaze ? 3 : 2);
        }
        foreach (var element in new UIElement[] { enabled, Card(sliders) }) Page.Children.Add(element);
        Actions.Children.Add(Button(Loc.Get("Reset"), () =>
        {
            StopManual();
            Navigate("Manual");
        }));
    }

    void PublishManual()
    {
        manualPublished = Environment.TickCount64;
        CalibrationSettings.WriteJson(Path.Combine(session.Root, "manual-output.json"), new JsonObject
        {
            ["expires"] = manualTesting ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + 1.5 : 0,
            ["values"] = manualTesting ? manualValues.DeepClone() : new JsonObject()
        });
    }

    void StopManual()
    {
        if (!manualTesting) return;
        manualTesting = false;
        PublishManual();
    }

    UIElement EyeOptions()
    {
        if (HeadsetModelExperiment.Standalone(session)) return HeadsetEyeOptions();
        var enabled = new CheckBox { Content = Loc.Get("IndependentEyeGaze"), IsChecked = session.IndependentGaze };
        AutomationProperties.SetHelpText(enabled, Loc.Get("TracksTheDirectionOfEachEyeSeparately"));
        var status = Muted("", live: true);
        var setup = Button(Loc.Get("SetUpEyeTracking"), () => Navigate("Setup"));
        var convergence = Button(Loc.Get("AdjustConvergence"), () => { outputParameter = "Gaze"; Navigate("Adjustments"); });
        trackingRefresh = () =>
        {
            var on = enabled.IsChecked == true;
            enabled.IsEnabled = setup.IsEnabled = !busy && !session.Running;
            status.Text = busy ? Loc.Get("WaitForTheCurrentStepToFinishToChange_85f9003a") : session.Running ? Loc.Get("StopTrackingToChangeEyeGaze")
                : on ? Loc.Get("StartsWithTracking") : Loc.Get("UsesStandardEyeTracking");
            setup.Visibility = Visible(on && !File.Exists(SetupService.EyeModel(session.Root)));
            convergence.Visibility = Visible(on);
        };
        enabled.Click += (_, _) =>
        {
            session.Save("independentGaze", enabled.IsChecked == true);
            trackingRefresh?.Invoke();
        };
        trackingRefresh();
        return Card(enabled, status, Buttons(setup, convergence));
    }

    UIElement HeadsetEyeOptions()
    {
        var sending = 0;
        var on = new CheckBox { Content = Loc.Get("IndependentEyeGaze") };
        AutomationProperties.SetHelpText(on, Loc.Get("TracksTheDirectionOfEachEyeSeparately"));
        var status = Muted("", live: true);
        var convergence = Button(Loc.Get("AdjustConvergence"), () => { outputParameter = "Gaze"; Navigate("Adjustments"); });
        void Refresh()
        {
            if (sending > 0) return;
            var settings = session.App?["settings"];
            on.IsEnabled = settings is not null;
            on.IsChecked = settings?["convergence"]?.GetValue<bool>() != false;
            convergence.Visibility = Visible(settings is not null && on.IsChecked == true);
            status.Text = session.AppProblem.Length > 0 ? session.AppProblem : settings is null ? Loc.Get("ReadingSettingsFromTheHeadset")
                : Loc.Get("SavedInQFTHeadsetChangingItRestartsTrackingFor");
        }
        on.Click += async (_, _) =>
        {
            sending++;
            try { await SendToHeadset("convergence", on.IsChecked == true); }
            finally { sending--; Refresh(); }
        };
        trackingRefresh += Refresh;
        Refresh();
        return Card(on, status, Buttons(convergence));
    }

    UIElement ConvergenceOptions()
    {
        if (HeadsetModelExperiment.Standalone(session)) return HeadsetConvergence();
        var gain = session.VergenceGain;
        var controls = new StackPanel();
        var strength = NumericSetting.AddTo(controls, Loc.Get("ConvergenceStrength"), 0, 3, gain, v =>
        {
            gain = v;
            SaveLater(() => session.SetVergenceGain(gain));
        }, scale: 100);
        (strength.SmallChange, strength.LargeChange, strength.TickFrequency, strength.IsSnapToTickEnabled) = (.05, .25, .05, true);
        controls.Children.Add(Button(Loc.Get("ResetTo100"), () => strength.Value = 1));
        controls.Children[^1].SetValue(MarginProperty, new Thickness(0, -8, 0, 0));
        var status = Muted("", live: true);
        var turnOn = Button(Loc.Get("TurnOnIndependentEyeGaze"), () => Navigate("Settings"));
        void Refresh()
        {
            var on = session.IndependentGaze;
            controls.IsEnabled = on && !busy && (!session.Running || session.State == "Connected");
            turnOn.Visibility = Visible(!on);
            status.Text = !on ? Loc.Get("WorksWithIndependentEyeGazeWhichIsOff") : controls.IsEnabled ? "" : Loc.Get("AvailableWhenTrackingIsReady");
            status.Visibility = Visible(status.Text.Length > 0);
        }
        trackingRefresh += Refresh;
        Refresh();
        return Card(Heading(Loc.Get("Convergence")), Muted(Loc.Get("HowMuchYourEyesTurnTowardEachOtherWhen")), controls, status, turnOn);
    }

    UIElement HeadsetConvergence()
    {
        var sending = 0;
        var syncing = false;
        async Task Apply(string key, object value)
        {
            sending++;
            try { await SendToHeadset(key, value); }
            finally { sending--; }
        }
        var controls = new StackPanel();
        var gain = 1.0;
        Action save = () => _ = Apply("vergenceGain", gain);
        var strength = NumericSetting.AddTo(controls, Loc.Get("ConvergenceStrength"), 0, 3, 1, v =>
        {
            if (!syncing) { gain = v; SaveLater(save); }
        }, scale: 100);
        (strength.SmallChange, strength.LargeChange, strength.TickFrequency, strength.IsSnapToTickEnabled) = (.05, .25, .05, true);
        controls.Children.Add(Button(Loc.Get("ResetTo100"), () => strength.Value = 1));
        controls.Children[^1].SetValue(MarginProperty, new Thickness(0, -8, 0, 0));
        var status = Muted("", live: true);
        var turnOn = Button(Loc.Get("TurnOnIndependentEyeGaze"), () => Navigate("Settings"));
        void Refresh()
        {
            if (sending > 0) return;
            var settings = session.App?["settings"];
            var on = settings?["convergence"]?.GetValue<bool>() != false;
            controls.IsEnabled = settings is not null && on;
            turnOn.Visibility = Visible(settings is not null && !on);
            if (settings?["vergenceGain"]?.GetValue<double>() is { } gain && !strength.IsMouseCaptureWithin && !strength.IsKeyboardFocusWithin)
            {
                syncing = true;
                strength.Value = gain;
                syncing = false;
            }
            status.Text = session.AppProblem.Length > 0 ? session.AppProblem : settings is null ? Loc.Get("ReadingSettingsFromTheHeadset")
                : on ? "" : Loc.Get("WorksWithIndependentEyeGazeWhichIsOff");
            status.Visibility = Visible(status.Text.Length > 0);
        }
        trackingRefresh += Refresh;
        Refresh();
        return Card(Heading(Loc.Get("Convergence")), Muted(Loc.Get("HowMuchYourEyesTurnTowardEachOtherWhen")), controls, status, turnOn);
    }

    (UIElement Card, Action Refresh, Action<string> Select) UseOptions(Action<string> changed)
    {
        var steamLink = SteamVr.IsSteamLink;
        var current = session.Use;
        var note = Muted(Loc.Get("HybridHandsPauseVirtualDesktopBodyTrackingWhileThey"));
        note.Visibility = Visible(current != "face");
        var status = Muted("", live: true);
        var radios = Uses.Select(use => (use.Id, Radio: Choice("use", use.Title, steamLink && use.Id == "hands" ? Loc.Get("RequiresVirtualDesktopSteamLinkUsesItsOwnHand") : use.About,
            current == use.Id, () =>
            {
                session.Save("use", use.Id);
                note.Visibility = Visible(use.Id != "face");
                changed(use.Id);
            }))).ToList();
        void Refresh()
        {
            foreach (var (id, radio) in radios) radio.IsEnabled = !busy && !session.Running && !(steamLink && id == "hands");
            status.Text = busy ? Loc.Get("WaitForTheCurrentStepToFinishToChange") : session.Running ? Loc.Get("StopTrackingToChangeThis") : "";
            status.Visibility = Visible(status.Text.Length > 0);
        }
        Refresh();
        return (Card([Heading(Loc.Get("WhatDoYouWantToUse")), .. radios.Select(r => r.Radio), note, status]), Refresh, id => radios.Single(r => r.Id == id).Radio.IsChecked = true);
    }

    Expander ProcessingOptions()
    {
        string[] devices = ["auto", "directml", "cpu"];
        var compute = new ComboBox { ItemsSource = new[] { Loc.Get("Automatic"), Loc.Get("GraphicsCardDirectML"), Loc.Get("ProcessorCPU") }, Margin = new(0, 0, 0, 20),
            SelectedIndex = Math.Max(0, Array.IndexOf(devices, session.Config["inferenceDevice"]?.GetValue<string>() ?? "auto")) };
        var graphics = new StackPanel { Visibility = Visible(compute.SelectedIndex != 2) };
        compute.SelectionChanged += (_, _) =>
        {
            graphics.Visibility = Visible(compute.SelectedIndex != 2);
            session.Save("inferenceDevice", devices[compute.SelectedIndex]);
        };
        var hardware = new StackPanel { Margin = new(0, 16, 0, 0) };
        Field(hardware, Loc.Get("TrackingProcessor"), compute);
        if (GraphicsAdapters.List() is { Count: > 1 } adapters)
        {
            var gpu = new ComboBox { ItemsSource = adapters.Select(a => a.Name).ToArray(), Margin = new(0, 0, 0, 8),
                SelectedIndex = Math.Max(0, adapters.FindIndex(a => a.Index == (session.Config["gpuIndex"]?.GetValue<int>() ?? 0))) };
            gpu.SelectionChanged += (_, _) => session.Save("gpuIndex", adapters[gpu.SelectedIndex].Index);
            Field(graphics, Loc.Get("GraphicsCard"), gpu);
        }
        hardware.Children.Add(graphics);
        hardware.Children.Add(Muted(Loc.Get("TakesEffectTheNextTimeTrackingStartsToLeave")));
        return new Ui.CardExpander { Header = Loc.Get("Processing"), Content = hardware, Margin = new(0, 0, 0, 16) };
    }

    void SetupProgress()
    {
        if (!session.SettingUp) return;
        if (session.Progress >= 100) (setupDone, setupStep) = (true, SetupSteps.Length);
        else if (session.State is not ("Setup paused" or "Couldn’t connect")) setupStep = Array.FindLastIndex(SetupSteps, s => s.From <= session.Progress);
    }

    void SetupOptions()
    {
        Subtitle.Text = Loc.Get("ConnectYourQuestProWithUSBTheFirstTime");
        Subtitle.Visibility = Visibility.Visible;
        ContentControl Row(string glyph, string title, string detail = "", string brush = "Muted", bool current = false, bool muted = true, string status = "") => new()
        {
            ContentTemplate = (DataTemplate)FindResource("SetupStatusRow"),
            Content = new { Glyph = glyph, Title = title, Detail = detail, Brush = brush, Muted = muted, Weight = current ? FontWeights.SemiBold : FontWeights.Normal,
                Name = status.Length == 0 ? title : $"{title}, {status}" }
        };
        Ui.Card Template(string name) => (Ui.Card)((DataTemplate)FindResource(name)).LoadContent();
        var use = session.Use;
        var (useCard, useRefresh, selectUse) = UseOptions(id =>
        {
            use = id;
            UseChanged(id);
            setupRefresh?.Invoke();
        });
        var headsetCard = Template("HeadsetPanel");
        var (found, caption, activity, search) = ((StackPanel)headsetCard.FindName("Headsets"), (TextBlock)headsetCard.FindName("Caption"),
            (ContentControl)headsetCard.FindName("Activity"), (Button)headsetCard.FindName("Search"));
        var hero = new HeadsetHero(150) { Margin = new(0, 4, 0, 12), HorizontalAlignment = HorizontalAlignment.Center };
        activity.Content = hero;
        search.Click += (_, _) => _ = Search();
        void ShowHeadsets()
        {
            found.Children.Clear();
            hero.Present(!SetUp);
            hero.Show(searching ? HeadsetHero.Look.Working : headsets.Any(q => q.State == "device") ? HeadsetHero.Look.Connected
                : headsets.Count > 0 || headsetsChecked is not null ? HeadsetHero.Look.Warning : HeadsetHero.Look.Idle);
            search.IsEnabled = !searching;
            search.Content = searching ? Loc.Get("Searching") : Loc.Get("SearchAgain");
            caption.Text = searching ? Loc.Get("SearchingUSBAndWiFi") : headsetsChecked is { } at ? Loc.Format("LastCheckedAt", at) : Loc.Get("NotCheckedYet");
            var preferred = session.Config["headsetSerial"]?.GetValue<string>() ?? "";
            found.Children.Add(Choice("headset", Loc.Get("AnyQuestPro"), Loc.Get("ConnectsToTheFirstQuestProItFinds"), preferred.Length == 0, () => session.Save("headsetSerial", "")));
            foreach (var group in headsets.Where(q => q.State == "device").GroupBy(q => q.Serial))
            {
                var ways = string.Join(Loc.Get("And"), group.OrderBy(q => q.Wireless ? 0 : 1).Select(q => q.Wireless ? Loc.Format("WiFi", q.Target) : "USB"));
                found.Children.Add(Choice("headset", Loc.Get("QuestProReady"), Loc.Format("ConnectedBySerial", ways, group.Key), preferred == group.Key, () => session.Save("headsetSerial", group.Key)));
            }
            if (preferred.Length > 0 && !headsets.Any(q => q.State == "device" && q.Serial == preferred))
                found.Children.Add(Choice("headset", Loc.Get("QuestProNotFoundRightNow"), Loc.Format("SerialWakeTheHeadsetThenSearchAgain", preferred), true, () => { }));
            foreach (var q in headsets.Where(q => q.State != "device"))
            {
                var (state, fix) = q.State == "unauthorized" ? (Loc.Get("WaitingForPermission"), Loc.Get("InTheHeadsetChooseAlwaysAllowFromThisComputer"))
                    : (Loc.Get("NotResponding"), Loc.Get("RestartTheHeadsetAndKeepUSBConnectedThenSearch"));
                found.Children.Add(Row("⚠", Loc.Format("QuestPro", state), $"{(q.Wireless ? $"Wi-Fi ({q.Target})" : "USB")}. {fix}", "Ink"));
            }
            if (!searching && headsetsChecked is not null && headsets.Count == 0)
                found.Children.Add(Muted(Loc.Get("NoHeadsetsFoundPlugInAUSBDataCable")));
        }
        async Task Search()
        {
            if (!searching && !preview)
            {
                searching = true;
                ShowHeadsets();
                try { (headsets, headsetsChecked) = (await session.Discover(), DateTime.Now); }
                catch (Exception error) { Error(Loc.Get("CouldnTSearchForHeadsets") + error.Message); }
                finally { searching = false; }
            }
            if (page == "Setup") setupRefresh?.Invoke();
        }
        var connectionMode = session.Config["connectionMode"]?.GetValue<string>() ?? "auto";
        var connection = Card([Heading(Loc.Get("Connection")), .. new[]
        {
            ("auto", Loc.Get("Automatic"), Loc.Get("UsesWiFiWhenItSAvailableAndUSB")),
            ("wifi", "Wi-Fi", Loc.Get("UnplugAfterSetupIfTheHeadsetRestartsConnectUSB")),
            ("usb", "USB", Loc.Get("KeepsTheCableConnectedMostReliable"))
        }.Select(c => Choice("connection", c.Item2, c.Item3, connectionMode == c.Item1, () => session.Save("connectionMode", c.Item1)))]);
        var steps = Template("SetupStepsPanel");
        var (status, bar, list) = ((TextBlock)steps.FindName("Status"), (ProgressBar)steps.FindName("Progress"), (StackPanel)steps.FindName("Steps"));
        var go = Button("", () => { }, true);
        void ShowSteps()
        {
            list.Children.Clear();
            for (var i = 0; i < SetupSteps.Length; i++)
            {
                var (from, name, about) = SetupSteps[i];
                var now = i == setupStep && !setupDone;
                var failed = now && setupProblem.Length > 0;
                var skipped = use == "hands" && from is 55 or 70 || from == 55 && !session.IndependentGaze;
                about = use == "hands" && from is 55 or 70 ? Loc.Get("SkippedFaceTrackingIsnTSelected")
                    : from == 55 && !session.IndependentGaze ? Loc.Get("SkippedWhileIndependentEyeGazeIsOffUsesStandard")
                    : from == 60 && use == "hands" ? Loc.Get("MakesSureSteamSteamVRAndVirtualDesktopAreInstalled") : about;
                var (glyph, brush, word) = failed ? ("⚠", "Ink", Loc.Get("CouldnTFinish")) : skipped ? ("–", "Muted", Loc.Get("Skipped")) : setupDone || i < setupStep ? ("✓", "Accent", Loc.Get("Done"))
                    : now && busy ? ("●", "Accent", Loc.Get("InProgress")) : now ? ("○", "Ink", Loc.Get("Paused")) : ("○", "Muted", Loc.Get("NotStarted"));
                list.Children.Add(Row(glyph, name, failed ? setupProblem : now && busy && !skipped && session.Detail.Length > 0 ? session.Detail : about,
                    brush, now && !skipped, !(now && busy) && !failed, word));
                var fixes = new WrapPanel { Margin = new(28, 0, 0, 6) };
                if (failed && moduleMissing)
                {
                    fixes.Children.Add(Button(Loc.Get("InstallModule"), () =>
                    {
                        session.Save("installModule", true);
                        _ = RunSetup();
                    }, true));
                    if (!SteamVr.IsSteamLink)
                    {
                        var hands = Button(Loc.Get("UseHandTrackingOnly"), () =>
                        {
                            selectUse("hands");
                            _ = RunSetup();
                        });
                        hands.Margin = new(8, 0, 0, 0);
                        fixes.Children.Add(hands);
                    }
                }
                else if (failed && session.HelpTarget is { } help)
                    fixes.Children.Add(Button(session.HelpCaption, () => Open(help)));
                if (fixes.Children.Count > 0) list.Children.Add(fixes);
            }
            useRefresh();
            bar.Visibility = Visible(busy);
            bar.Value = session.Progress;
            status.Text = busy && setupStep >= 0 && setupStep < SetupSteps.Length ? Loc.Format("StepOf", setupStep + 1, SetupSteps.Length, SetupSteps[setupStep].Title)
                : setupDone ? Loc.Get("SetupCompleteChooseStartToBeginTracking") : setupProblem.Length > 0 ? Loc.Get("SetupCouldnTFinishTheStepBelowShowsWhat")
                : setupStep >= 0 ? Loc.Get("SetupPausedYourProgressIsSaved") : Loc.Get("RunSetupOncePerHeadsetOrAgainAfterAn");
            go.Content = busy ? Loc.Get("Cancel") : setupDone ? Loc.Get("SetUpAgain") : setupProblem.Length > 0 ? Loc.Get("TryAgain") : setupStep >= 0 ? Loc.Get("Continue") : Loc.Get("SetUpAction");
            go.Appearance = moduleMissing && !busy ? Ui.ControlAppearance.Secondary : Ui.ControlAppearance.Primary;
        }
        async Task RunSetup()
        {
            if (busy) return;
            (busy, setupDone, setupProblem, moduleMissing, setupStep) = (true, false, "", false, 0);
            StartButton.Content = Loc.Get("Cancel");
            ClearNotice();
            ShowSteps();
            try { await session.Prepare(); }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                (setupProblem, moduleMissing) = (error.Message, error is ModuleNotInstalledException);
                Error(error.Message);
            }
            finally
            {
                busy = false;
                SidebarStatus();
                if (page == "Setup")
                {
                    setupRefresh?.Invoke();
                    _ = Search();
                }
            }
        }
        go.Click += async (_, _) =>
        {
            if (busy) session.CancelSetup();
            else await RunSetup();
        };
        setupRefresh = () =>
        {
            SetupProgress();
            ShowSteps();
            ShowHeadsets();
        };
        var log = Path.Combine(session.Root, "setup.log");
        var openLog = Button(Loc.Get("OpenSetupLog"), () => Open(log));
        (openLog.Margin, openLog.IsEnabled) = (new(0, 8, 0, 0), File.Exists(log));
        var tips = Stack([.. new[]
        {
            Loc.Get("UseAUSBCableThatCarriesDataChargeOnly"),
            Loc.Get("TurnOnDeveloperModeForYourHeadsetInThe"),
            Loc.Get("InTheHeadsetAllowUSBDebuggingAndChooseAlways"),
            Loc.Get("InMagiskSuperuserAllowShell"),
            Loc.Get("ForWiFiKeepThisPCAndTheHeadset"),
            Loc.Get("IfAHeadsetShowsNotRespondingRestartItAnd")
        }.Select(tip => Row("•", tip)), openLog]);
        foreach (var element in new UIElement[] { useCard, headsetCard, connection, steps, new Ui.CardExpander { Header = Loc.Get("CanTFindYourHeadset"), Content = tips, Margin = new(0, 0, 0, 8) } })
            Page.Children.Add(element);
        Actions.Children.Add(go);
        ShowSteps();
        ShowHeadsets();
        if (!busy) _ = Search();
    }
}
