using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Ui = Wpf.Ui.Controls;

namespace QFTPlus;

public partial class StudioWindow : Window
{
    static readonly string[] Pages = ["Tracking", "Adjustments", "Manual", "Calibration", "Cameras", "Setup", "Settings"];
    internal readonly Session session;
    readonly bool preview;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) }, saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly HashSet<Action> pendingSaves = [];
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(1) };
    readonly System.Windows.Forms.NotifyIcon? tray;
    readonly List<(Image Image, string Key)> cameras = [];
    string page = "Tracking";
    string? revealLog;
    internal bool busy;
    bool quitting;
    Action? trackingRefresh;

    public StudioWindow(string root, bool preview = false, string? config = null)
    {
        InitializeComponent();
        this.preview = preview;
        session = new(root, config);
        saveTimer.Tick += (_, _) => SaveNow();
        Closed += (_, _) => SaveNow();
        session.Changed += _ => Dispatcher.Invoke(() =>
        {
            SidebarStatus();
            if (session.SettingUp) SetupProgress();
            trackingRefresh?.Invoke();
            if (page == "Setup") setupRefresh?.Invoke();
            else if (page != "Tracking" && busy && session.Detail.Length > 0) Error(session.Detail);
        });
        foreach (var name in Pages)
        {
            var item = new ListBoxItem { Content = new TextBlock { Text = TitleOf(name) }, Tag = name, Margin = new(0, name == "Setup" ? 16 : 0, 0, 0) };
            AutomationProperties.SetName(item, TitleOf(name));
            TextSearch.SetText(item, TitleOf(name));
            Navigation.Items.Add(item);
        }
        Navigation.SelectionChanged += (_, _) => { if (Navigation.SelectedItem is ListBoxItem { Tag: string name } && name != page) Navigate(name); };
        ApplyPreferences();
        InitializeUpdates();
        UseChanged(session.Use);
        Navigate("Tracking");
        RestorePlacement();
        SizeChanged += (_, _) => ResizeGuide();
        if (!preview)
        {
            void Raise(Action? then = null) => Dispatcher.Invoke(() => { Show(); then?.Invoke(); Activate(); });
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open QFT+", null, (_, _) => Raise(() => WindowState = WindowState.Normal));
            menu.Items.Add("Settings", null, (_, _) => Raise(() => Navigate("Settings")));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Quit QFT+", null, async (_, _) => await Quit());
            tray = new() { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Text = "QFT+", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (_, _) => Raise();
            timer.Tick += async (_, _) => await Tick();
            timer.Start();
            UserPreferenceChangedEventHandler changed = (_, _) => Dispatcher.BeginInvoke(ApplyPreferences);
            SystemEvents.UserPreferenceChanged += changed;
            Closed += (_, _) => SystemEvents.UserPreferenceChanged -= changed;
        }
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || e.Key != Key.OemComma) return;
            Navigate("Settings");
            e.Handled = true;
        };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (recording && cancel is { IsVisible: true, IsEnabled: true }) cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            else if (busy && Equals(StartButton.Content, "Cancel")) session.CancelSetup();
            else return;
            e.Handled = true;
        };
        Closing += (_, e) =>
        {
            StopManual();
            if (quitting || preview) return;
            SavePlacement();
            if (busy) session.CancelSetup();
            else if (recording) Error("Finish or cancel calibration before closing this window.");
            else if (training || session.Running) Hide();
            else
            {
                quitting = true;
                tray?.Dispose();
                Application.Current.Shutdown();
                return;
            }
            e.Cancel = true;
        };
    }

    static string TitleOf(string page) => page == "Manual" ? "Test movements" : page;

    internal void RestorePlacement()
    {
        if (session.Config["window"] is not JsonObject saved) return;
        double Get(string key) => saved[key] is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) ? number : double.NaN;
        var (left, top, width, height) = (Get("left"), Get("top"), Get("width"), Get("height"));
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (double.IsNaN(left + top + width + height) || !screen.Contains(new Point(left + 100, top + 16))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (left, top, Math.Max(MinWidth, width), Math.Max(MinHeight, height));
        if (saved["maximized"]?.GetValue<bool>() == true) WindowState = WindowState.Maximized;
    }

    internal void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty) return;
        try
        {
            session.Save("window", new JsonObject { ["left"] = bounds.Left, ["top"] = bounds.Top, ["width"] = bounds.Width, ["height"] = bounds.Height,
                ["maximized"] = WindowState == WindowState.Maximized && restoreState is null });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    void ApplyPreferences()
    {
        SetTextScale(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor", 100) is int scale ? scale / 100.0 : 1);
        if (!(recording && kind == "pupils")) Theme("System");
    }

    void UseChanged(string use)
    {
        foreach (var item in Navigation.Items.Cast<ListBoxItem>().Where(item => item.Tag is "Adjustments" or "Manual" or "Calibration" or "Cameras"))
            item.Visibility = use == "hands" ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void Error(string message)
    {
        Notice.Text = message;
        NoticeBox.Visibility = Visibility.Visible;
        SetupHelp.Visibility = session.HelpTarget is null ? Visibility.Collapsed : Visibility.Visible;
        SetupHelp.Content = session.HelpCaption;
    }

    void HelpClick(object sender, RoutedEventArgs e)
    {
        if (session.HelpTarget is { } target) Open(target);
    }

    void ClearNotice() => NoticeBox.Visibility = Visibility.Collapsed;

    void SaveLater(Action save)
    {
        pendingSaves.Add(save);
        saveTimer.Stop();
        saveTimer.Start();
    }

    internal void SaveNow()
    {
        saveTimer.Stop();
        var saves = pendingSaves.ToArray();
        pendingSaves.Clear();
        foreach (var save in saves)
            try { save(); }
            catch (Exception error) { Error(error.Message); }
    }

    internal async Task Start(bool fromModule = false)
    {
        if (busy) return;
        busy = true;
        StartButton.Content = "Cancel";
        ClearNotice();
        (setupDone, setupProblem, moduleMissing, setupStep) = (false, "", false, -1);
        trackingRefresh?.Invoke();
        try
        {
            await session.Start(fromModule);
            ClearNotice();
        }
        catch (OperationCanceledException)
        {
            session.Notify("Ready");
            ClearNotice();
        }
        catch (Exception error)
        {
            (setupProblem, moduleMissing) = (error.Message, error is ModuleNotInstalledException);
            if (page != "Tracking") Error(error.Message);
            session.Notify("Couldn’t start", error.Message);
        }
        finally
        {
            busy = false;
            StartButton.IsEnabled = true;
            SidebarStatus();
            trackingRefresh?.Invoke();
            setupRefresh?.Invoke();
        }
    }

    async void StartClick(object sender, RoutedEventArgs e)
    {
        if (busy) session.CancelSetup();
        else if (!session.Running) await Start();
        else if (recording || training) Error("Finish calibration before stopping tracking.");
        else
        {
            StopManual();
            busy = true;
            StartButton.IsEnabled = false;
            try { await session.Stop(); }
            catch (Exception error) { Error(error.Message); }
            finally
            {
                busy = false;
                StartButton.IsEnabled = true;
                SidebarStatus();
                trackingRefresh?.Invoke();
            }
        }
    }

    async Task Quit()
    {
        if (busy || recording || training)
        {
            Show();
            if (busy) session.CancelSetup();
            Error(busy ? "Finishing the current step before quitting." : "Finish or cancel calibration before quitting.");
            return;
        }
        StartButton.IsEnabled = false;
        SavePlacement();
        try
        {
            await session.Stop();
            quitting = true;
            timer.Stop();
            tray?.Dispose();
            http.Dispose();
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            Show();
            Error(error.Message);
            StartButton.IsEnabled = true;
        }
    }

    Ui.Button Button(string title, Action action, bool primary = false)
    {
        var button = new Ui.Button { Content = title, HorizontalAlignment = HorizontalAlignment.Left, Appearance = primary ? Ui.ControlAppearance.Primary : Ui.ControlAppearance.Secondary };
        button.Click += (_, _) => action();
        return button;
    }

    Ui.Button AsyncButton(string title, Func<Task> action) => Button(title, async () =>
    {
        try { await action(); }
        catch (Exception error) { Error(error.Message); }
    });

    TextBlock Text(string value, double size = 14, bool muted = false, bool live = false)
    {
        var text = new TextBlock { Text = value, Margin = new(0, 0, 0, 8) };
        text.SetResourceReference(TextBlock.FontSizeProperty, "Font" + size);
        if (muted) text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        if (live) AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        return text;
    }

    TextBlock Muted(string value, bool live = false) => Text(value, 13, true, live);

    TextBlock Heading(string value, double size = 16)
    {
        var heading = Text(value, size);
        heading.SetResourceReference(StyleProperty, "SectionHeading");
        return heading;
    }

    static StackPanel Stack(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    WrapPanel Buttons(params Button[] buttons)
    {
        var panel = new WrapPanel();
        foreach (var button in buttons)
        {
            button.Margin = new(0, 0, 8, 8);
            panel.Children.Add(button);
        }
        return panel;
    }

    static Ui.Card Card(params UIElement[] children) => new() { Content = children.Length == 1 ? children[0] : Stack(children) };

    CheckBox Check(string key, string label, bool fallback, Action? changed = null)
    {
        var box = new CheckBox { Content = label, IsChecked = session.Config[key]?.GetValue<bool>() ?? fallback };
        box.Click += (_, _) =>
        {
            session.Save(key, box.IsChecked == true);
            changed?.Invoke();
        };
        return box;
    }

    internal void Navigate(string name)
    {
        if (recording)
        {
            if (name != page) Error("Finish or cancel calibration first.");
            return;
        }
        StopManual();
        setupRefresh = trackingRefresh = adjustmentRefresh = updateRefresh = null;
        page = name;
        Page.Children.Clear();
        Actions.Children.Clear();
        cameras.Clear();
        ClearNotice();
        SaveNow();
        PageTitle.Text = TitleOf(name);
        Title = PageTitle.Text + " — QFT+";
        Subtitle.Visibility = Visibility.Collapsed;
        PageScroll.ScrollToTop();
        Navigation.SelectedItem = Navigation.Items.Cast<ListBoxItem>().Single(item => Equals(item.Tag, name));
        Navigation.ScrollIntoView(Navigation.SelectedItem);
        switch (name)
        {
            case "Tracking": Home(); break;
            case "Adjustments": Adjustments(); break;
            case "Manual": Manual(); break;
            case "Calibration": Calibration(); break;
            case "Cameras": Cameras(); break;
            case "Setup": SetupOptions(); break;
            case "Settings": Settings(); break;
        }
        RefreshUpdates();
        SidebarStatus();
    }

    bool Problem => !busy && !session.Running && (setupProblem.Length > 0 || session.State == "Tracking stopped");

    (string Glyph, string Brush) Mark() => Problem ? ("", "Ink") : session.State == "Connected" ? ("", "Accent")
        : busy || session.Running ? ("", "Accent") : ("", "Muted");

    void SidebarStatus()
    {
        var (glyph, brush) = Mark();
        ConnectionMark.Text = glyph;
        ConnectionMark.SetResourceReference(TextBlock.ForegroundProperty, brush);
        Connection.Text = Problem && setupProblem.Length > 0 ? "Couldn’t start" : session.State;
        StartButton.Content = busy && !session.Running ? "Cancel" : session.Running ? "Stop" : "Start";
    }

    void Cameras()
    {
        Subtitle.Text = session.Running ? "Waiting for cameras…" : "Start tracking to view cameras.";
        Subtitle.Visibility = Visibility.Visible;
        var outline = new CheckBox { Content = "Show pupil outlines", IsChecked = true };
        outline.Click += (_, _) =>
        {
            for (var i = 0; i < 2; i++) cameras[i] = (cameras[i].Image, (outline.IsChecked == true ? "pupil" : "camera") + i);
        };
        var grid = new UniformGrid { Columns = 2 };
        foreach (var (key, title) in new[] { ("pupil0", "Left eye"), ("pupil1", "Right eye"), ("camera2", "Left face"), ("camera3", "Right face"), ("camera4", "Brow") })
        {
            var image = new Image { Height = 180, Stretch = Stretch.Uniform };
            AutomationProperties.SetName(image, title + " camera");
            cameras.Add((image, key));
            var card = Card(Text(title), image);
            card.Margin = new(0, 8, 12, 8);
            grid.Children.Add(card);
        }
        Page.Children.Add(outline);
        Page.Children.Add(grid);
    }

    async Task UpdateCameras()
    {
        var shown = await Task.WhenAll(cameras.ToArray().Select(async camera =>
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelHeight = (int)Math.Ceiling(camera.Image.Height * VisualTreeHelper.GetDpi(camera.Image).DpiScaleY);
                bitmap.StreamSource = new MemoryStream(await http.GetByteArrayAsync("http://127.0.0.1:8081/" + camera.Key + ".jpg"));
                bitmap.EndInit();
                bitmap.Freeze();
                camera.Image.Source = bitmap;
                return true;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
            {
                camera.Image.Source = null;
                return false;
            }
        }));
        if (page != "Cameras") return;
        Subtitle.Text = session.Running ? "Waiting for cameras…" : "Start tracking to view cameras.";
        Subtitle.Visibility = shown.Contains(true) ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void SetTextScale(double scale)
    {
        scale = Math.Clamp(scale, 1, 2.25);
        FontSize = 14 * scale;
        SidebarColumn.Width = new(Math.Min(280, 196 * scale));
        foreach (var size in new[] { 12, 13, 14, 16, 18, 23, 25, 30 }) Resources["Font" + size] = Math.Max(13, size) * scale;
        Resources["ControlContentThemeFontSize"] = Resources["ContentControlFontSize"] = 14 * scale;
    }

    internal void Theme(string name)
    {
        var dark = name == "Dark" || name == "System" && ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.Glow or SystemTheme.CapturedMotion;
        ApplicationThemeManager.Apply(SystemParameters.HighContrast ? ApplicationTheme.HighContrast : dark ? ApplicationTheme.Dark : ApplicationTheme.Light, Ui.WindowBackdropType.None);
        foreach (var (key, resource) in new[] { ("Canvas", "ApplicationBackgroundBrush"), ("Surface", "CardBackgroundFillColorDefaultBrush"),
                     ("Sidebar", "SolidBackgroundFillColorBaseBrush"), ("Ink", "TextFillColorPrimaryBrush"), ("Muted", "TextFillColorSecondaryBrush"),
                     ("Line", "DividerStrokeColorDefaultBrush"), ("Accent", "AccentFillColorDefaultBrush"), ("OnAccent", "TextOnAccentFillColorPrimaryBrush") })
            Resources[key] = FindResource(resource);
        foreach (var (key, alias) in new[] { ("ListBoxItemSelectedBackgroundThemeBrush", "Surface"), ("ListBoxItemSelectedForegroundThemeBrush", "Ink"), ("MenuBarItemBackgroundSelected", "Line") })
            if (SystemParameters.HighContrast) Resources.Remove(key); else Resources[key] = Resources[alias];
    }

    static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    bool SetUp => busy || session.Running || string.Equals(session.Config["root"]?.GetValue<string>(), session.Root, StringComparison.OrdinalIgnoreCase);

    void TrackingActionClick(object sender, RoutedEventArgs e)
    {
        if (!SetUp || Problem && moduleMissing) Navigate("Setup");
        else StartClick(sender, e);
    }

    void ViewCameras(object sender, RoutedEventArgs e) => Navigate("Cameras");

    void ViewTrackingLog(object sender, RoutedEventArgs e)
    {
        revealLog = session.State == "Tracking stopped" ? session.ErrorLog : setupStep >= 0 && !setupDone ? "setup.log" : "tracking.log";
        Navigate("Settings");
    }

    void Home()
    {
        var use = session.Use;
        var config = session.Config;
        bool On(string key, bool fallback = false) => config[key]?.GetValue<bool>() ?? fallback;
        var header = Text("Features", 14, true);
        header.FontWeight = FontWeights.SemiBold;
        header.Margin = new(4, 8, 0, 8);
        AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
        var list = new StackPanel();
        Ui.CardAction Row(string title, Ui.SymbolRegular icon, Action go)
        {
            var row = new Ui.CardAction { Tag = (title, icon), Margin = new(0, 0, 0, 4), Padding = new(16, 12, 14, 4) };
            row.Click += (_, _) => go();
            list.Children.Add(row);
            return row;
        }
        void Show(Ui.CardAction row, string value, bool problem = false)
        {
            var (title, icon) = ((string, Ui.SymbolRegular))row.Tag;
            (row.Content, row.Icon) = (Stack(Text(title), Text(value, 14, true)), new Ui.SymbolIcon(problem ? Ui.SymbolRegular.Warning24 : icon) { FontSize = 20 });
            AutomationProperties.SetName(row, $"{title}, {value}");
        }
        void Calibrate(string area)
        {
            kind = area;
            Navigate("Calibration");
        }
        if (use == "hands")
            Show(Row("Face tracking", Ui.SymbolRegular.Emoji24, () => Navigate("Settings")), "Off");
        else
        {
            var needsEyeSetup = session.IndependentGaze && !File.Exists(SetupService.EyeModel(session.Root));
            Show(Row("Eye gaze", Ui.SymbolRegular.Eye24, () => Navigate(needsEyeSetup ? "Setup" : "Settings")),
                !session.IndependentGaze ? "Standard" : needsEyeSetup ? "Needs setup" : "Independent", needsEyeSetup);
            Show(Row("Cheeks, tongue and brows", Ui.SymbolRegular.EmojiSmileSlight24, () => Calibrate("enroll")),
                !On("extraFaceOutput", true) && !On("tongueOutput", true) ? "Off" : File.Exists(config["faceEnrollment"]?.GetValue<string>()) ? "Calibrated" : "Standard");
            Show(Row("Pupil dilation", Ui.SymbolRegular.EyeTracking24, () => Calibrate("pupils")),
                !File.Exists(Path.Combine(session.Root, "calibration/qpro-pupil-dilation.json")) ? "Not calibrated" : On("pupilDilation") ? "Calibrated" : "Off");
        }
        var hybridStopped = false;
        var hybrid = Row("Hybrid hands", Ui.SymbolRegular.HandLeft24, () =>
        {
            if (hybridStopped || session.HybridProblem.Length > 0) revealLog = "hybrid.log";
            Navigate("Settings");
        });
        foreach (var element in new UIElement[] { TrackingOverview, header, list }) Page.Children.Add(element);
        trackingRefresh = () =>
        {
            var failed = !busy && !session.Running && setupProblem.Length > 0;
            var via = session.Detail.EndsWith("USB") ? "USB" : "Wi-Fi";
            var (title, about) = failed ? ("Couldn’t start tracking", setupProblem)
                : session.State == "Tracking stopped" ? ("Tracking stopped", session.Detail)
                : session.State == "Connected" && use == "hands" ? ("Hand tracking is on", $"Connected over {via}. Hybrid hands and controllers are on in Virtual Desktop.")
                : session.State == "Connected" ? ("Tracking is on", $"Connected over {via}. Expressions are going to VRCFaceTracking." + (session.HybridReady ? " Hybrid hands are on." : ""))
                : busy || session.Running ? (session.State, session.Detail)
                : !SetUp ? ("Set up Quest Pro", "Connect the headset to this PC with a USB data cable. Setup runs once and takes a few minutes.")
                : ("Ready to track", "Put on the headset, then start tracking.");
            TrackingStatus.Severity = Problem ? Ui.InfoBarSeverity.Error : session.State == "Connected" ? Ui.InfoBarSeverity.Success : Ui.InfoBarSeverity.Informational;
            (TrackingStatus.Title, TrackingStatus.Message) = (title, about);
            TrackingProgress.Visibility = Visible(busy && session.SettingUp || session.State == "Connecting");
            TrackingProgress.IsIndeterminate = !session.SettingUp;
            TrackingProgress.Value = session.Progress;
            TrackingAction.Content = busy && !session.Running ? "Cancel" : session.Running ? "Stop tracking" : !SetUp ? "Set up…"
                : Problem ? moduleMissing ? "Open Setup" : "Try again" : "Start tracking";
            TrackingAction.IsEnabled = session.State != "Stopping";
            TrackingAction.Appearance = session.Running || busy ? Ui.ControlAppearance.Secondary : Ui.ControlAppearance.Primary;
            ViewCamerasButton.Visibility = Visible(session.Running);
            TrackingHelp.Visibility = Visible(failed && session.HelpTarget is not null);
            TrackingHelp.Content = session.HelpCaption;
            TrackingLogs.Visibility = Visible(Problem);
            hybridStopped = session.Hybrid is not null && !Session.Alive(session.Hybrid);
            var hybridFailed = session.HybridProblem.Length > 0;
            Show(hybrid, use == "face" ? "Off" : SteamVr.IsSteamLink ? "Requires Virtual Desktop" : hybridFailed ? "Couldn’t start" : hybridStopped ? "Stopped" : "On",
                hybridStopped || hybridFailed);
        };
        trackingRefresh();
    }

    static Visibility Visible(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    void Settings()
    {
        var faceOnly = new List<UIElement>();
        UIElement Face(UIElement element)
        {
            faceOnly.Add(element);
            return element;
        }
        void ShowFace(string use)
        {
            foreach (var element in faceOnly) element.Visibility = Visible(use != "hands");
            UseChanged(use);
        }
        session.DisableUncalibratedOutputs();
        FaceLogCheck.IsChecked = session.Config["faceLog"]?.GetValue<bool>() == true;
        testerCard.DataContext = new { IsTester = session.Tester, Id = session.TesterId };
        TesterConsent.IsChecked = false;
        LogView.Open(session.Root, revealLog);
        var (useCard, useRefresh, _) = UseOptions(ShowFace);
        var eyes = EyeOptions();
        trackingRefresh += useRefresh;
        const string selfInstall = "Turn off only if you install the module yourself.";
        var installModule = Check("installModule", "Install the VRCFaceTracking module", true);
        AutomationProperties.SetHelpText(installModule, selfInstall);
        UpdateOptions();
        foreach (var element in new[]
        {
            Heading("Tracking", 18), useCard, Face(eyes), Face(Card(OutputOptions())), Card(new ThumbrestSettings(session)),
            Card(Heading("Startup"), Check("setupOnStart", "Set up the headset on Start", true), Face(Stack(installModule, Muted(selfInstall))),
                Face(Check("startWithVrcft", "Start with VRCFaceTracking", true)), Check("openVrApps", "Open VR apps on Start", true)),
            Face(ProcessingOptions()), Face(FaceCalibrationOptions), Face(testerCard), LogView
        })
            Page.Children.Add(element);
        UninstallOptions();
        ShowFace(session.Use);
        if (revealLog is not null) Dispatcher.BeginInvoke(() => PageScroll.ScrollToVerticalOffset(LogView.TranslatePoint(new Point(), Page).Y), DispatcherPriority.Loaded);
        revealLog = null;
    }

    StackPanel OutputOptions()
    {
        var rows = new StackPanel();
        void Reload()
        {
            if (session.Running) Send("reload");
        }
        foreach (var (key, title) in new[] { ("tongueOutput", "Tongue"), ("extraFaceOutput", "Extra expressions"), ("pupilDilation", "Pupil dilation") })
        {
            var calibrated = session.Calibrated(key);
            var option = new CheckBox { Content = calibrated ? title : title + " · Calibrate first", IsEnabled = calibrated,
                IsChecked = calibrated && (session.Config[key]?.GetValue<bool>() ?? key != "pupilDilation") };
            AutomationProperties.SetName(option, title);
            rows.Children.Add(option);
            if (key != "extraFaceOutput")
            {
                option.Click += (_, _) =>
                {
                    session.Save(key, option.IsChecked == true);
                    Reload();
                };
                continue;
            }
            var groups = CalibrationSettings.FaceGroups.Select(g => new CheckBox { Content = g.Title, Tag = g.Kind, IsChecked = session.FaceOn(g.Kind), Margin = new(28, 0, 0, 0) }).ToList();
            void Mixed() => option.IsChecked = groups.All(g => g.IsChecked == true) ? true : groups.Any(g => g.IsChecked == true) ? null : false;
            void Save(IEnumerable<CheckBox> changed)
            {
                foreach (var box in changed) session.Save(CalibrationSettings.FaceOutputKey((string)box.Tag), box.IsChecked == true);
                session.Save("extraFaceOutput", groups.Any(g => g.IsChecked == true));
                Reload();
            }
            option.Click += (_, _) =>
            {
                var on = !groups.All(g => g.IsChecked == true);
                groups.ForEach(g => g.IsChecked = on);
                Mixed();
                Save(groups);
            };
            foreach (var box in groups)
            {
                box.Click += (_, _) =>
                {
                    Mixed();
                    Save([box]);
                };
                rows.Children.Add(box);
            }
            Mixed();
        }
        return rows;
    }

    void ReloadSettings()
    {
        var offset = PageScroll.VerticalOffset;
        Navigate("Settings");
        Dispatcher.BeginInvoke(() => PageScroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    void FaceLogClick(object sender, RoutedEventArgs e)
    {
        session.Save("faceLog", FaceLogCheck.IsChecked == true);
        Send("reload");
    }

    void RecordBenchmark(object sender, RoutedEventArgs e)
    {
        kind = "benchmark-quick";
        Navigate("Calibration");
    }

    void JoinTesting(object sender, RoutedEventArgs e)
    {
        if (TesterConsent.IsChecked != true) return;
        session.JoinTesting();
        ReloadSettings();
    }

    void LeaveTesting(object sender, RoutedEventArgs e)
    {
        session.LeaveTesting();
        ReloadSettings();
    }

    void ShareRecordings(object sender, RoutedEventArgs e)
    {
        Navigate("Settings");
        Dispatcher.BeginInvoke(() => testerCard.BringIntoView(), DispatcherPriority.Loaded);
    }
}
