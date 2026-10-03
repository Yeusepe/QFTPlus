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
        (0, "Find your Quest Pro", "Looks for your headset over USB and Wi-Fi."),
        (10, "Allow access", "Approve USB debugging in the headset, then allow Shell in Magisk → Superuser."),
        (25, "Turn on Wi-Fi", "Switches the headset to Wi-Fi so you can unplug the cable. Skipped when Wi-Fi is already on."),
        (35, "Install components", "One-time install of the tracking runtime on this PC. This can take a few minutes."),
        (55, "Prepare eye tracking", "Builds the eye model from your headset. Keep the headset awake."),
        (60, "Check VR apps", "Makes sure Steam, SteamVR, and VRCFaceTracking are installed."),
        (70, "Update VRCFaceTracking", "Installs the QFT+ module. VRCFaceTracking closes briefly if it’s open.")
    ];
    static readonly (string Id, string Title, string About)[] Uses =
    [
        ("face", "Face and eye tracking", "Sends your expressions and eye movement to VRCFaceTracking."),
        ("hands", "Hybrid hands and controllers", "Hand tracking and controllers together in Virtual Desktop. Doesn’t use VRCFaceTracking."),
        ("both", "Both", "Face and eye tracking, plus hybrid hands whenever you connect with Virtual Desktop.")
    ];
    bool manualTesting, searching;
    long manualPublished;
    JsonObject manualValues = new();
    string outputParameter = "*", manualGroup = "Tongue";
    internal Action? setupRefresh, adjustmentRefresh;
    internal int setupStep = -1;
    internal bool setupDone, moduleMissing;
    internal string setupProblem = "";
    List<SetupService.Headset> headsets = [];
    DateTime? headsetsChecked;
    sealed record ChoiceContent(string Title, string About);

    string[] Parameters() => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(session.Root, "tracking-parameters.json"))) ?? [];
    static string Label(string key) => Regex.Replace(key, "([a-z])([A-Z])", "$1 $2");
    static double Neutral(string name) => name.StartsWith("Pupil") ? .5 : name.StartsWith("Openness") ? 1 : 0;

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
        string[] names = single ? [outputParameter] : members;
        void Choose(string choice)
        {
            outputParameter = choice;
            Navigate("Adjustments");
        }
        var areaPicker = Picker(["All areas", .. areas], area ?? "All areas", i => Choose(i == 0 ? "*" : areas[i - 1]), () => Page.Children.OfType<ComboBox>().First());
        areaPicker.Margin = new(0, 0, 0, area is null ? 24 : 16);
        Field(Page, "Area", areaPicker);
        if (area is not null)
        {
            var parameterPicker = Picker(["All parameters", .. members.Select(Label)], single ? Label(outputParameter) : "All parameters",
                i => Choose(i == 0 ? area : members[i - 1]), () => Page.Children.OfType<ComboBox>().ElementAt(1));
            parameterPicker.Margin = new(0, 0, 0, 24);
            Field(Page, "Parameter", parameterPicker);
        }
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
        var values = (config[outputParameter] as JsonObject ?? new()).DeepClone().AsObject();
        double Value(string key, double fallback, bool inherit = true) =>
            OutputAdjustments.Number(values, key, OutputAdjustments.Number(group, key, inherit ? OutputAdjustments.Number(inherited, key, fallback) : fallback));
        var gaze = area == "Gaze";
        var (minimum, maximum, scale, format, unit, decimals) = gaze ? (-1.2, 1.2, 1.0, "0.###", "", 3) : (0.0, 1.0, 100.0, "0.##'%'", "%", 2);
        var status = Muted("", live: true);
        var readout = Text("", 16);
        AutomationProperties.SetName(readout, "Live adjustment values");
        Page.Children.Add(area is null || single ? Card(status, readout) : Card(status));
        if (area is not null && !single) Page.Children.Add(new Ui.CardExpander { Header = "Live values", Content = readout, Margin = new(0, -8, 0, 16) });
        var problem = Text("", 13, live: true);
        problem.Visibility = Visibility.Collapsed;
        Page.Children.Add(problem);
        JsonNode? edited = null;
        var selected = outputParameter;
        void Save(string key, double value)
        {
            values[key] = value;
            var valid = Value("inputMax", maximum, false) - Value("inputMin", minimum, false) >= .0001 && Value("outputMin", minimum, false) <= Value("outputMax", maximum, false);
            problem.Text = valid ? "" : "Input minimum must be below input maximum. Output minimum cannot exceed output maximum. Changes are not saved until the ranges are valid.";
            problem.Visibility = Visible(!valid);
            if (!valid) return;
            edited = values.DeepClone();
            SaveLater(Write);
        }
        void Write()
        {
            if (Load() is not { } saved) return;
            saved[selected] = edited;
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
        var scope = area is null ? "These settings apply to every parameter unless an area or parameter changes them."
            : single ? $"Only settings you change here override {area} and All areas."
            : $"These settings apply to every parameter in {area}. Only settings you change here override All areas.";
        Page.Children.Add(Muted(modeled ? scope : scope + (single ? " QFT+’s camera models don’t track this parameter, so it always comes from the headset."
            : " QFT+’s camera models don’t track these parameters, so they always come from the headset.")));
        var paired = !single && names.Any(n => OutputAdjustments.Partner(n) is not null);
        if (modeled || paired)
        {
            var input = new StackPanel();
            if (modeled)
            {
                var about = "Sends the headset’s own tracking instead of QFT+’s camera models. The adjustments below still apply."
                    + (area is null or "Pupils" ? " The headset doesn’t measure pupils, so they stay at 50%." : "");
                var passthrough = Toggle("passthrough", "Headset passthrough");
                AutomationProperties.SetHelpText(passthrough, about);
                var note = Muted(about);
                note.Margin = new(0, 0, 0, paired ? 20 : 0);
                input.Children.Add(passthrough);
                input.Children.Add(note);
            }
            if (paired)
            {
                var match = new ComboBox { ItemsSource = new[] { "Off", "Average both sides", "Follow the stronger side" },
                    SelectedIndex = (int)Math.Clamp(Math.Round(Value("match", 0)), 0, 2), Margin = new(0, 0, 0, 8) };
                match.SelectionChanged += (_, _) => Save("match", match.SelectedIndex);
                Field(input, "Match left and right", match);
                input.Children.Add(Muted("Moves the two sides of each pair together. Averaging evens out one-sided jitter; following the stronger side keeps blinks together."));
            }
            Page.Children.Add(Card(input));
        }
        var basics = new StackPanel();
        NumericSetting.AddTo(basics, "Strength", 0, 10, Value("strength", 1), v => Save("strength", v), scale: 100);
        Number(basics, "Offset", "offset", minimum - maximum, maximum - minimum, 0);
        Number(basics, "Dead zone", "deadzone", 0, maximum - minimum, 0);
        basics.Children.Add(Muted("Dead zone ignores movement around neutral, then scales the remaining movement to reach full output. Offset shifts the result after this step."));
        Slider? release = null;
        var syncing = false;
        NumericSetting.AddTo(basics, "Smoothing", 0, 100, Value("smoothing", 0), v =>
        {
            Save("smoothing", v);
            if (release is null || values.ContainsKey("release") || group.ContainsKey("release") || inherited.ContainsKey("release")) return;
            syncing = true;
            release.Value = v;
            syncing = false;
        });
        Page.Children.Add(Card(basics));
        var response = new StackPanel { Margin = new(0, 16, 0, 0) };
        Number(response, "Response curve", "curve", .1, 5, 1, raw: true);
        response.Children.Add(Muted("1 is linear. Below 1 boosts small movements; above 1 makes them gentler."));
        release = NumericSetting.AddTo(response, "Smoothing when relaxing", 0, 100, Value("release", Value("smoothing", 0)), v =>
        {
            if (!syncing) Save("release", v);
        });
        response.Children.Add(Muted("Used while an expression returns to rest. Higher values let it fade out slowly. Matches Smoothing until you change it."));
        response.Children.Add(Toggle("invert", "Invert output"));
        Page.Children.Add(new Ui.CardExpander { Header = "Response", Content = response, Margin = new(0, 0, 0, 16) });
        if (area is not null)
        {
            var limits = new StackPanel { Margin = new(0, 16, 0, 0) };
            limits.Children.Add(Muted(gaze ? "Gaze values use radians. Set the movement you can comfortably reach, then limit the output sent to VRCFaceTracking."
                : "Set the input range you can comfortably reach. Output limits clamp the adjusted result; equal limits hold a fixed value."));
            Number(limits, "Input minimum", "inputMin", minimum, maximum, minimum, false);
            Number(limits, "Input maximum", "inputMax", minimum, maximum, maximum, false);
            if (names.Select(Neutral).Distinct().ToArray() is [var rest])
            {
                Number(limits, "Input neutral", "neutral", minimum, maximum, rest, false);
                limits.Children.Add(Muted("Neutral is your resting input, clamped to the input range. Pupils default to 50%, open eyelids to 100%, and other parameters to 0%."));
            }
            Number(limits, "Output minimum", "outputMin", minimum, maximum, minimum, false);
            Number(limits, "Output maximum", "outputMax", minimum, maximum, maximum, false);
            Page.Children.Add(new Ui.CardExpander { Header = "Input and output limits", Content = limits, IsExpanded = single, Margin = new(0, 0, 0, 16) });
        }
        Page.Children.Add(Muted("Live values show the QFT+ module’s output. Your avatar must support the selected expression; VRCFaceTracking’s own adjustments can change it further."));
        string[] own = single ? [] : members.Where(m => config[m] is JsonObject { Count: > 0 }).ToArray();
        if (own.Length > 0)
            Page.Children.Add(Muted((own.Length == 1 ? $"{Label(own[0])} has its own settings, which take precedence here." : $"{own.Length} parameters have their own settings, which take precedence here.")
                + " Select one and choose Use inherited settings to remove them."));
        adjustmentRefresh = () =>
        {
            try { File.WriteAllBytes(Path.Combine(session.Root, "output-status.lease"), []); }
            catch (IOException) { }
            JsonObject data;
            try { data = Session.Read(Path.Combine(session.Root, "output-status.json")); }
            catch (InvalidDataException) { data = new(); }
            var age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - OutputAdjustments.Number(data, "updated", 0);
            if (age < 0 || age > 2)
            {
                status.Text = "No live response from the QFT+ module. Open VRCFaceTracking; if it is already running, update the module in Setup and restart VRCFaceTracking.";
                readout.Text = "Waiting for live values";
                return;
            }
            status.Text = JsonNode.DeepEquals(data["settings"], Load()) ? "Changes applied by the QFT+ module." : "Changes saved. Waiting for the module to apply them…";
            if (area is null)
            {
                readout.Text = "Select an area to see its live values.";
                return;
            }
            var inputs = data["inputs"] as JsonObject ?? new();
            var outputs = data["outputs"] as JsonObject ?? new();
            string Shown(JsonObject from, string name) => (OutputAdjustments.Number(from, name, 0) * scale).ToString(format);
            var missing = names.Where(name => !inputs.ContainsKey(name) || !outputs.ContainsKey(name)).ToArray();
            readout.Text = string.Join("\n", names.Except(missing).Select(name => $"{Label(name)}: {Shown(inputs, name)} → {Shown(outputs, name)}")
                .Concat(missing.Length == 0 ? [] : [$"Not provided by the QFT+ module: {string.Join(", ", missing.Select(Label))}. Check its eye and face modules in VRCFaceTracking."]));
            if (area == "Pupils")
                status.Text += data["pupilTracking"]?.GetValue<bool>() == true ? " Pupil tracking is live. Dilation combines both eyes."
                    : " Pupil tracking is off or has no fresh data: input stays at 50%. Offset and output limits still apply. Dilation combines both eyes.";
        };
        adjustmentRefresh();
        Actions.Children.Add(Button(outputParameter == "*" ? "Reset defaults" : "Use inherited settings", () =>
        {
            SaveNow();
            if (Load() is { } data && data.Remove(outputParameter)) CalibrationSettings.WriteJson(path, data);
            Navigate("Adjustments");
        }));
    }

    void Manual()
    {
        var choices = OutputAdjustments.Areas.Where(area => Parameters().Any(name => OutputAdjustments.Area(name) == area)).ToArray();
        var select = Picker(choices, manualGroup, i =>
        {
            StopManual();
            manualGroup = choices[i];
            Navigate("Manual");
        }, () => Page.Children.OfType<ComboBox>().First());
        select.Margin = new(0, 0, 0, 12);
        Field(Page, "Area", select);
        manualValues = new();
        var sliders = new StackPanel { IsEnabled = false };
        var enabled = new CheckBox { Content = "Override live tracking", IsChecked = false };
        enabled.Click += (_, _) =>
        {
            manualTesting = enabled.IsChecked == true;
            if (manualTesting && !Processes.Running("VRCFaceTracking"))
            {
                enabled.IsChecked = manualTesting = false;
                Error("Open VRCFaceTracking to test movements.");
            }
            sliders.IsEnabled = manualTesting;
            PublishManual();
        };
        var about = Muted("While it’s on, VRCFaceTracking gets these values instead of your face.");
        about.Margin = new(0, 0, 0, 16);
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
        foreach (var element in new UIElement[] { enabled, about, Card(sliders) }) Page.Children.Add(element);
        Actions.Children.Add(Button("Reset", () =>
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
        var enabled = new CheckBox { Content = "Independent eye gaze", IsChecked = session.IndependentGaze };
        var status = Muted("", live: true);
        var setup = Button("Set up eye tracking", () => Navigate("Setup"));
        var gain = session.VergenceGain;
        var controls = new StackPanel();
        var strength = NumericSetting.AddTo(controls, "Convergence strength", 0, 3, gain, v =>
        {
            gain = v;
            SaveLater(() => session.SetVergenceGain(gain));
        }, scale: 100);
        (strength.SmallChange, strength.LargeChange, strength.TickFrequency, strength.IsSnapToTickEnabled) = (.05, .25, .05, true);
        controls.Children.Add(Button("Reset to 100%", () => strength.Value = 1));
        var adjustmentStatus = Muted("", live: true);
        var options = Stack(Muted("Adjust how much your eyes turn toward each other when looking at something nearby."), controls,
            Muted("100% uses the calibrated movement. Higher values increase it. At 0%, both eyes look in the same direction. Depth accuracy is experimental."), adjustmentStatus);
        options.Margin = new(0, 12, 0, 0);
        trackingRefresh = () =>
        {
            var on = enabled.IsChecked == true;
            enabled.IsEnabled = setup.IsEnabled = !busy && !session.Running;
            status.Text = busy ? "Wait for the current step to finish to change eye gaze." : session.Running ? "Stop tracking to change eye gaze."
                : on ? "Starts with tracking." : "Uses standard eye tracking.";
            AutomationProperties.SetHelpText(enabled, "Tracks the direction of each eye separately. " + status.Text);
            setup.Visibility = Visible(on && !File.Exists(SetupService.EyeModel(session.Root)));
            controls.IsEnabled = on && !busy && (!session.Running || session.State == "Connected");
            adjustmentStatus.Text = !on ? "Turn on independent eye gaze to adjust convergence."
                : busy || session.Running && session.State != "Connected" ? "Available when tracking is ready."
                : session.Running ? "Changes apply immediately and are saved automatically." : "Saved automatically. Applies when tracking starts.";
        };
        enabled.Click += (_, _) =>
        {
            session.Save("independentGaze", enabled.IsChecked == true);
            trackingRefresh?.Invoke();
        };
        trackingRefresh();
        return Card(enabled, Muted("Tracks the direction of each eye separately."), status, setup,
            new Expander { Header = "Eye adjustments", Content = options, Margin = new(0, 8, 0, 0) });
    }

    (UIElement Card, Action Refresh, Action<string> Select) UseOptions(Action<string> changed)
    {
        var steamLink = SteamVr.IsSteamLink;
        var current = session.Use;
        var note = Muted("Hybrid hands pause Virtual Desktop body tracking while they’re on.");
        note.Visibility = Visible(current != "face");
        var status = Muted("", live: true);
        var radios = Uses.Select(use => (use.Id, Radio: Choice("use", use.Title, steamLink && use.Id == "hands" ? "Requires Virtual Desktop. Steam Link uses its own hand tracking." : use.About,
            current == use.Id, () =>
            {
                session.Save("use", use.Id);
                note.Visibility = Visible(use.Id != "face");
                changed(use.Id);
            }))).ToList();
        void Refresh()
        {
            foreach (var (id, radio) in radios) radio.IsEnabled = !busy && !session.Running && !(steamLink && id == "hands");
            status.Text = busy ? "Wait for the current step to finish to change this." : session.Running ? "Stop tracking to change this." : "";
            status.Visibility = Visible(status.Text.Length > 0);
        }
        Refresh();
        return (Card([Heading("What do you want to use?"), .. radios.Select(r => r.Radio), note, status]), Refresh, id => radios.Single(r => r.Id == id).Radio.IsChecked = true);
    }

    Expander ProcessingOptions()
    {
        string[] devices = ["auto", "directml", "cpu"];
        var compute = new ComboBox { ItemsSource = new[] { "Automatic", "Graphics card (DirectML)", "Processor (CPU)" }, Margin = new(0, 0, 0, 20),
            SelectedIndex = Math.Max(0, Array.IndexOf(devices, session.Config["inferenceDevice"]?.GetValue<string>() ?? "auto")) };
        var graphics = new StackPanel { Visibility = Visible(compute.SelectedIndex != 2) };
        compute.SelectionChanged += (_, _) =>
        {
            graphics.Visibility = Visible(compute.SelectedIndex != 2);
            session.Save("inferenceDevice", devices[compute.SelectedIndex]);
        };
        var hardware = new StackPanel { Margin = new(0, 16, 0, 0) };
        Field(hardware, "Tracking processor", compute);
        if (GraphicsAdapters.List() is { Count: > 1 } adapters)
        {
            var gpu = new ComboBox { ItemsSource = adapters.Select(a => a.Name).ToArray(), Margin = new(0, 0, 0, 8),
                SelectedIndex = Math.Max(0, adapters.FindIndex(a => a.Index == (session.Config["gpuIndex"]?.GetValue<int>() ?? 0))) };
            gpu.SelectionChanged += (_, _) => session.Save("gpuIndex", adapters[gpu.SelectedIndex].Index);
            Field(graphics, "Graphics card", gpu);
        }
        hardware.Children.Add(graphics);
        hardware.Children.Add(Muted("Takes effect the next time tracking starts. To leave more performance for VR, choose Graphics card, then a different card from the one running your game."));
        return new Ui.CardExpander { Header = "Processing", Content = hardware, Margin = new(0, 0, 0, 16) };
    }

    void SetupProgress()
    {
        if (!session.SettingUp) return;
        if (session.Progress >= 100) (setupDone, setupStep) = (true, SetupSteps.Length);
        else if (session.State is not ("Setup paused" or "Couldn’t connect")) setupStep = Array.FindLastIndex(SetupSteps, s => s.From <= session.Progress);
    }

    void SetupOptions()
    {
        Subtitle.Text = "Connect your Quest Pro with USB the first time. After that, QFT+ connects over Wi-Fi.";
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
            (ProgressBar)headsetCard.FindName("Activity"), (Button)headsetCard.FindName("Search"));
        search.Click += (_, _) => _ = Search();
        void ShowHeadsets()
        {
            found.Children.Clear();
            activity.Visibility = Visible(searching);
            search.IsEnabled = !searching;
            search.Content = searching ? "Searching…" : "Search again";
            caption.Text = searching ? "Searching USB and Wi-Fi…" : headsetsChecked is { } at ? $"Last checked at {at:t}." : "Not checked yet.";
            var preferred = session.Config["headsetSerial"]?.GetValue<string>() ?? "";
            found.Children.Add(Choice("headset", "Any Quest Pro", "Connects to the first Quest Pro it finds.", preferred.Length == 0, () => session.Save("headsetSerial", "")));
            foreach (var group in headsets.Where(q => q.State == "device").GroupBy(q => q.Serial))
            {
                var ways = string.Join(" and ", group.OrderBy(q => q.Wireless ? 0 : 1).Select(q => q.Wireless ? $"Wi-Fi ({q.Target})" : "USB"));
                found.Children.Add(Choice("headset", "Quest Pro · Ready", $"Connected by {ways}. Serial {group.Key}.", preferred == group.Key, () => session.Save("headsetSerial", group.Key)));
            }
            if (preferred.Length > 0 && !headsets.Any(q => q.State == "device" && q.Serial == preferred))
                found.Children.Add(Choice("headset", "Quest Pro · Not found right now", $"Serial {preferred}. Wake the headset, then search again.", true, () => { }));
            foreach (var q in headsets.Where(q => q.State != "device"))
            {
                var (state, fix) = q.State == "unauthorized" ? ("Waiting for permission", "In the headset, choose “Always allow from this computer,” then Allow.")
                    : ("Not responding", "Restart the headset and keep USB connected, then search again.");
                found.Children.Add(Row("⚠", $"Quest Pro · {state}", $"{(q.Wireless ? $"Wi-Fi ({q.Target})" : "USB")}. {fix}", "Ink"));
            }
            if (!searching && headsetsChecked is not null && headsets.Count == 0)
                found.Children.Add(Muted("No headsets found. Plug in a USB data cable and put on the headset to wake it."));
        }
        async Task Search()
        {
            if (!searching && !preview)
            {
                searching = true;
                ShowHeadsets();
                try { (headsets, headsetsChecked) = (await session.Discover(), DateTime.Now); }
                catch (Exception error) { Error("Couldn’t search for headsets. " + error.Message); }
                finally { searching = false; }
            }
            if (page == "Setup") ShowHeadsets();
        }
        var connectionMode = session.Config["connectionMode"]?.GetValue<string>() ?? "auto";
        var connection = Card([Heading("Connection"), .. new[]
        {
            ("auto", "Automatic", "Uses Wi-Fi when it’s available and USB otherwise."),
            ("wifi", "Wi-Fi", "Unplug after setup. If the headset restarts, connect USB once to turn Wi-Fi back on."),
            ("usb", "USB", "Keeps the cable connected. Most reliable.")
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
                about = use == "hands" && from is 55 or 70 ? "Skipped. Face tracking isn’t selected."
                    : from == 55 && !session.IndependentGaze ? "Skipped while independent eye gaze is off. Uses standard eye tracking."
                    : from == 60 && use == "hands" ? "Makes sure Steam, SteamVR, and Virtual Desktop are installed." : about;
                var (glyph, brush, word) = failed ? ("⚠", "Ink", "Couldn’t finish") : skipped ? ("–", "Muted", "Skipped") : setupDone || i < setupStep ? ("✓", "Accent", "Done")
                    : now && busy ? ("●", "Accent", "In progress") : now ? ("○", "Ink", "Paused") : ("○", "Muted", "Not started");
                list.Children.Add(Row(glyph, name, failed ? setupProblem : now && busy && !skipped && session.Detail.Length > 0 ? session.Detail : about,
                    brush, now && !skipped, !(now && busy) && !failed, word));
                var fixes = new WrapPanel { Margin = new(28, 0, 0, 6) };
                if (failed && moduleMissing)
                {
                    fixes.Children.Add(Button("Install module", () =>
                    {
                        session.Save("installModule", true);
                        _ = RunSetup();
                    }, true));
                    if (!SteamVr.IsSteamLink)
                    {
                        var hands = Button("Use hand tracking only", () =>
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
            status.Text = busy && setupStep >= 0 && setupStep < SetupSteps.Length ? $"Step {setupStep + 1} of {SetupSteps.Length}: {SetupSteps[setupStep].Title}"
                : setupDone ? "Setup complete. Choose Start to begin tracking." : setupProblem.Length > 0 ? "Setup couldn’t finish. The step below shows what to do."
                : setupStep >= 0 ? "Setup paused. Your progress is saved." : "Run setup once per headset, or again after an update.";
            go.Content = busy ? "Cancel" : setupDone ? "Set up again" : setupProblem.Length > 0 ? "Try again" : setupStep >= 0 ? "Continue" : "Set up";
            go.Appearance = moduleMissing && !busy ? Ui.ControlAppearance.Secondary : Ui.ControlAppearance.Primary;
        }
        async Task RunSetup()
        {
            if (busy) return;
            (busy, setupDone, setupProblem, moduleMissing, setupStep) = (true, false, "", false, 0);
            StartButton.Content = "Cancel";
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
                    ShowSteps();
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
        };
        var log = Path.Combine(session.Root, "setup.log");
        var openLog = Button("Open setup log", () => Open(log));
        (openLog.Margin, openLog.IsEnabled) = (new(0, 8, 0, 0), File.Exists(log));
        var tips = Stack([.. new[]
        {
            "Use a USB cable that carries data. Charge-only cables don’t work.",
            "Turn on Developer Mode for your headset in the Meta Horizon app.",
            "In the headset, allow USB debugging and choose “Always allow from this computer.”",
            "In Magisk → Superuser, allow Shell.",
            "For Wi-Fi, keep this PC and the headset on the same network.",
            "If a headset shows “Not responding,” restart it and keep USB connected."
        }.Select(tip => Row("•", tip)), openLog]);
        foreach (var element in new UIElement[] { useCard, headsetCard, connection, steps, new Ui.CardExpander { Header = "Can’t find your headset?", Content = tips, Margin = new(0, 0, 0, 8) } })
            Page.Children.Add(element);
        Actions.Children.Add(go);
        ShowSteps();
        ShowHeadsets();
        if (!busy) _ = Search();
    }
}
