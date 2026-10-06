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

public partial class StudioWindow : Ui.FluentWindow
{
    static readonly string[] Pages = ["Tracking", "Adjustments", "Manual", "Calibration", "Cameras", "Setup", "Settings"];
    static readonly string[] PageIcons = ["face", "tune", "experiment", "frame_person", "photo_camera", "head_mounted_device", "settings"];
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
    readonly HeadsetHero trackingHero = new(150) { Margin = new(0, 4, 0, 16), HorizontalAlignment = HorizontalAlignment.Center };
    internal readonly HeadsetHero sidebarHero = new(120)
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 42, 0, 0),
        OpacityMask = new LinearGradientBrush(Colors.Black, Colors.Transparent, new Point(.5, .43), new Point(.5, .68)),
    };

    public StudioWindow(string root, bool preview = false, string? config = null)
    {
        InitializeComponent();
        TrackingCard.Children.Insert(0, trackingHero);
        DeviceArea.Children.Insert(1, sidebarHero);
        DeviceGlow.Background = sidebarHero.Glow;
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
            var label = new DockPanel();
            var text = new TextBlock { Text = TitleOf(name), VerticalAlignment = VerticalAlignment.Center };
            var glyph = Symbols.Shape(PageIcons[Array.IndexOf(Pages, name)], 22, text);
            glyph.Margin = new(0, 0, 14, 0);
            label.Children.Add(glyph);
            label.Children.Add(text);
            var item = new ListBoxItem { Content = label, Tag = name, Margin = new(0, name == "Setup" ? 16 : 0, 0, 4) };
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
        PageScroll.ScrollChanged += (_, _) => FadeEdges();
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
            if (Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.OemComma or Key.F)) return;
            if (e.Key == Key.F) SearchBox.Focus();
            else Navigate("Settings");
            e.Handled = true;
        };
        InitializeSearch();
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
            else _ = Quit();
            e.Cancel = true;
        };
    }

    void FadeEdges()
    {
        var height = PageScroll.ActualHeight;
        if (height <= 0) return;
        var fade = Math.Min(.25, 32 / height);
        var (above, below) = (PageScroll.VerticalOffset > 0, PageScroll.VerticalOffset < PageScroll.ScrollableHeight);
        var mask = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1) };
        foreach (var (color, offset) in new[] { (above ? Colors.Transparent : Colors.Black, 0), (Colors.Black, fade), (Colors.Black, 1 - fade), (below ? Colors.Transparent : Colors.Black, 1) })
            mask.GradientStops.Add(new GradientStop(color, offset));
        mask.Freeze();
        PageScroll.OpacityMask = mask;
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
            item.Visibility = Visible(use != "hands" && !(item.Tag is "Cameras" && HeadsetModelExperiment.Standalone(session)));
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
        if (!SetUp && !busy) { Navigate("Setup"); return; }
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

    TextBlock Heading(string value, double size = 18)
    {
        var heading = Text(value, size);
        heading.Tag = "Heading";
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

    static FrameworkElement Card(params UIElement[] children)
    {
        if (children is [TextBlock { Tag: "Heading" } heading, .. var rest] && rest.Length > 0)
        {
            heading.Margin = new(0, 8, 0, 12);
            return Stack(heading, Card(rest));
        }
        if (children[^1] is TextBlock { Margin: var margin } last) last.Margin = new(margin.Left, margin.Top, margin.Right, 0);
        return new Ui.Card { Content = children.Length == 1 ? children[0] : Stack(children) };
    }

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
        page = name;
        SearchBox.Clear();
        Clear(TitleOf(name));
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

    void Clear(string title)
    {
        StopManual();
        setupRefresh = trackingRefresh = adjustmentRefresh = updateRefresh = null;
        Page.Children.Clear();
        Actions.Children.Clear();
        cameras.Clear();
        ClearNotice();
        SaveNow();
        PageTitle.Text = title;
        Title = title + " — QFT+";
        Subtitle.Visibility = Visibility.Collapsed;
        PageScroll.ScrollToTop();
    }

    bool Problem => !busy && !session.Running && (setupProblem.Length > 0 || session.State == "Tracking stopped");

    Action deviceGo = () => { };

    void DeviceClick(object sender, RoutedEventArgs e) => deviceGo();

    string AppIssue => session.State == "Connected" && HeadsetModelExperiment.Standalone(session) && session.Detail.IndexOf(" · Meta", StringComparison.Ordinal) is var at and >= 0
        ? session.Detail[(at + 3)..].Replace(" · ", ". ") + "." : "";

    (HeadsetHero.Look Look, string Status, Action Go) Attention()
    {
        void Tracking() => Navigate("Tracking");
        var hands = session.HybridProblem.Length > 0 || session.Hybrid is not null && !Session.Alive(session.Hybrid);
        if (Problem) return (HeadsetHero.Look.Error, setupProblem.Length > 0 ? "Couldn’t start" : session.State, Tracking);
        if (session.State == "Waiting for headset") return (HeadsetHero.Look.Warning, session.State, Tracking);
        if (session.State == "Connected" && AppIssue.Length > 0) return (HeadsetHero.Look.Warning, "Tracking needs attention", Tracking);
        if (hands) return (HeadsetHero.Look.Warning, "Hands need attention", () => { revealLog = "hybrid.log"; Navigate("Settings"); });
        if (session.State == "Connected" && session.tracking?.SlowCause is { Length: > 0 }) return (HeadsetHero.Look.Warning, "Running slowly", Tracking);
        return (session.State == "Connected" ? HeadsetHero.Look.Connected : busy || session.Running ? HeadsetHero.Look.Working : HeadsetHero.Look.Idle, session.State, Tracking);
    }

    void SidebarStatus()
    {
        var (look, status, go) = Attention();
        (Connection.Text, deviceGo) = (status, go);
        AutomationProperties.SetName(DeviceButton, $"Quest Pro, {status}");
        if (SetUp) DeviceButton.Visibility = Visibility.Visible;
        sidebarHero.Present(SetUp, () => DeviceButton.Visibility = Visibility.Collapsed);
        sidebarHero.Hands = session.Hands;
        sidebarHero.Show(look);
        StartButton.Content = !SetUp && !busy ? "Set up…" : busy && !session.Running ? "Cancel" : session.Running ? "Stop" : "Start";
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
        SidebarColumn.Width = new(Math.Min(320, 240 * scale));
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
        if (!SystemParameters.HighContrast)
            foreach (var (key, color) in dark ? DarkPalette : LightPalette) Resources[key] = Paint(color);
        foreach (var (key, alias) in new[] { ("ListBoxItemSelectedBackgroundThemeBrush", "Selected"), ("ListBoxItemSelectedForegroundThemeBrush", "OnSelected"), ("MenuBarItemBackgroundSelected", "Line") })
            if (SystemParameters.HighContrast) Resources.Remove(key); else Resources[key] = Resources[alias];
    }

    static readonly (string Key, string Color)[] DarkPalette = [("Canvas", "#414141-#272727"), ("Sidebar", "#0DFFFFFF"), ("Menu", "#4A4A4A"), ("MenuLine", "#26FFFFFF"), ("Surface", "#0EFFFFFF"),
        ("Ink", "#FFFFFF"), ("Muted", "#BEBEBE"), ("Line", "#14FFFFFF"), ("Selected", "#FFFFFF"), ("OnSelected", "#1F1F1F"), ("Hover", "#14FFFFFF"),
        ("SwitchOn", "#2AD116"), ("SwitchOff", "#40FFFFFF"), ("Primary", "#FFFFFF"), ("OnPrimary", "#1F1F1F"), ("Secondary", "#1AFFFFFF")];
    static readonly (string Key, string Color)[] LightPalette = [("Canvas", "#F7F7F9-#E6E6EA"), ("Sidebar", "#08000000"), ("Menu", "#FFFFFF"), ("MenuLine", "#1F000000"), ("Surface", "#FFFFFF"),
        ("Ink", "#1C1C1E"), ("Muted", "#5C5D66"), ("Line", "#14000000"), ("Selected", "#1C1C1E"), ("OnSelected", "#FFFFFF"), ("Hover", "#0D000000"),
        ("SwitchOn", "#178A0C"), ("SwitchOff", "#40000000"), ("Primary", "#1C1C1E"), ("OnPrimary", "#FFFFFF"), ("Secondary", "#0F000000")];
    static Brush Paint(string color)
    {
        Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
        Brush brush = color.Split('-') is [var top, var bottom] ? new LinearGradientBrush(Parse(top), Parse(bottom), 90) : new SolidColorBrush(Parse(color));
        brush.Freeze();
        return brush;
    }

    static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    bool SetUp => busy || session.Running || string.Equals(session.Config["root"]?.GetValue<string>(), session.Root, StringComparison.OrdinalIgnoreCase);

    void TrackingSwitchClick(object sender, RoutedEventArgs e)
    {
        StartClick(sender, e);
        trackingRefresh?.Invoke();
    }

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
        var header = Section("Features");
        var list = new StackPanel();
        Ui.CardAction Row(string title, string icon, Action go)
        {
            var row = ListRow(title, icon, go);
            list.Children.Add(row);
            return row;
        }
        void Show(Ui.CardAction row, string value, bool problem = false) => ShowRow(row, value, problem);
        void Calibrate(string area)
        {
            kind = area;
            Navigate("Calibration");
        }
        Action? appRows = null;
        if (use == "hands")
            Show(Row("Face tracking", "face", () => Navigate("Settings")), "Off");
        else
        {
            var onHeadset = HeadsetModelExperiment.Standalone(session);
            var needsEyeSetup = !onHeadset && session.IndependentGaze && !File.Exists(SetupService.EyeModel(session.Root));
            var eyes = Row("Eye gaze", "eye_tracking", () => Navigate(needsEyeSetup ? "Setup" : "Settings"));
            if (!onHeadset) Show(eyes, !session.IndependentGaze ? "Standard" : needsEyeSetup ? "Needs setup" : "Independent", needsEyeSetup);
            var face = Row("Cheeks, tongue and brows", "sentiment_satisfied", () => Calibrate("enroll"));
            var pupils = Row("Pupil dilation", "adjust", () => Calibrate("pupils"));
            if (!onHeadset)
            {
                Show(face, !On("extraFaceOutput", true) && !On("tongueOutput", true) ? "Off" : File.Exists(config["faceEnrollment"]?.GetValue<string>()) ? "Calibrated" : "Standard");
                Show(pupils, !File.Exists(Path.Combine(session.Root, "calibration/qpro-pupil-dilation.json")) ? "Not calibrated" : On("pupilDilation") ? "Calibrated" : "Off");
            }
            else appRows = () =>
            {
                string Value(string key) => session.App is null ? "Unknown" : !HeadsetApp.Flag(session.App, "calibrated", key) ? "Not calibrated"
                    : HeadsetApp.Flag(session.App, "settings", key == "face" ? "tongue" : "pupils") ? "Calibrated" : "Off";
                Show(eyes, session.App is null ? "Unknown" : HeadsetApp.Flag(session.App, "settings", "convergence") ? "Independent" : "Standard");
                Show(face, Value("face"));
                Show(pupils, Value("pupils"));
            };
        }
        var hybridStopped = false;
        var hybrid = Row("Hybrid hands", "front_hand", () =>
        {
            if (hybridStopped || session.HybridProblem.Length > 0) revealLog = "hybrid.log";
            Navigate("Settings");
        });
        foreach (var element in new UIElement[] { TrackingOverview, header, list }) Page.Children.Add(element);
        RoundEnds(list);
        trackingRefresh = () =>
        {
            var failed = !busy && !session.Running && setupProblem.Length > 0;
            var onHeadset = HeadsetModelExperiment.Standalone(session);
            var appIssue = AppIssue;
            appRows?.Invoke();
            var via = session.Detail.EndsWith("USB") ? "USB" : "Wi-Fi";
            var (slow, fps) = (session.tracking?.SlowCause ?? "", session.tracking?.Stream?.Fps ?? 0);
            var (title, about) = failed ? ("Couldn’t start tracking", setupProblem)
                : session.State == "Tracking stopped" ? ("Tracking stopped", session.Detail)
                : session.State == "Connected" && appIssue.Length > 0 ? ("Tracking needs attention", appIssue + " Expressions can’t follow your face until the headset’s own face and eye tracking are available.")
                : session.State == "Connected" && onHeadset ? ("Tracking is on", "QFT+ Headset is tracking and sending expressions to VRCFaceTracking.")
                : session.State == "Waiting for headset" && onHeadset ? ("Can’t reach the headset", session.Detail)
                : session.State == "Connected" && use == "hands" ? ("Hand tracking is on", $"Connected over {via}. Hybrid hands and controllers are on in Virtual Desktop.")
                : session.State == "Connected" && slow.Length > 0 ? ("Tracking is running slowly", slow == "pc"
                    ? $"This PC is keeping up with only about {fps:0} camera frames a second, so expressions lag. Close other demanding apps."
                    : $"Only about {fps:0} camera frames a second are arriving over {via}, so expressions lag. "
                      + (via == "USB" ? "Try another USB port or cable." : "Connect the headset to this PC with a USB cable, or move closer to your router."))
                : session.State == "Connected" ? ("Tracking is on", $"Connected over {via}. Expressions are going to VRCFaceTracking." + (session.HybridReady ? " Hybrid hands are on." : ""))
                : busy || session.Running ? (session.State, session.Detail)
                : !SetUp ? ("Set up Quest Pro", "Connect the headset to this PC with a USB data cable. Setup runs once and takes a few minutes.")
                : session.State == "Ready" && session.Detail.Length > 0 ? ("Tracking is off", session.Detail)
                : ("Ready to track", onHeadset ? "Put on the headset, then start tracking. QFT+ Headset tracks there and sends expressions to this PC." : "Put on the headset, then start tracking.");
            (TrackingTitle.Text, TrackingMessage.Text) = (title, about);
            TrackingMessage.Visibility = Visible(about.Length > 0);
            trackingHero.Present(!SetUp);
            var needsSetup = !SetUp || Problem && moduleMissing;
            TrackingAction.Content = !SetUp ? "Set up…" : "Open Setup";
            TrackingAction.Appearance = Ui.ControlAppearance.Primary;
            TrackingAction.Visibility = Visible(needsSetup);
            TrackingSwitch.Visibility = Visible(!needsSetup);
            TrackingSwitch.IsChecked = busy || session.Running;
            TrackingSwitch.IsEnabled = session.State != "Stopping" && !recording;
            ViewCamerasButton.Visibility = Visible(session.Running && !onHeadset);
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

    TextBlock Section(string title)
    {
        var header = Text(title, 18);
        header.FontWeight = FontWeights.SemiBold;
        header.Margin = new(0, 16, 0, 12);
        AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level2);
        return header;
    }

    Ui.CardAction ListRow(string title, string? icon, Action go)
    {
        var row = new Ui.CardAction { Tag = (title, icon), Margin = new(0, 0, 0, 2), Padding = new(24, 14, 20, 6), BorderThickness = new(0), IsChevronVisible = false };
        row.SetResourceReference(BackgroundProperty, "Surface");
        row.Click += (_, _) => go();
        return row;
    }

    void ShowRow(Ui.CardAction row, string value, bool problem = false)
    {
        var (title, icon) = ((string, string?))row.Tag;
        var name = Text(title);
        var chevron = Symbols.Shape("chevron_right", 22, name);
        chevron.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(chevron, Dock.Right);
        var content = new DockPanel();
        content.Children.Add(chevron);
        content.Children.Add(value.Length > 0 ? Stack(name, Text(value, 14, true)) : name);
        (row.Content, row.Icon) = (content, icon is null ? null : Symbols.Icon(problem ? "warning" : icon, (Brush)FindResource("Ink")));
        row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(row, value.Length > 0 ? $"{title}, {value}" : title);
    }

    Border ActionRow(string title, TextBlock value, string icon, Button action)
    {
        action.VerticalAlignment = VerticalAlignment.Center;
        action.Margin = new(16, 0, 0, 2);
        DockPanel.SetDock(action, Dock.Right);
        var glyph = Symbols.Icon(icon, (Brush)FindResource("Ink"));
        glyph.Margin = new(0, 0, 14, 2);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var content = new DockPanel();
        foreach (var child in new UIElement[] { action, glyph, Stack(Text(title), value) }) content.Children.Add(child);
        var row = new Border { Child = content, Margin = new(0, 0, 0, 2), Padding = new(24, 14, 20, 6) };
        row.SetResourceReference(Border.BackgroundProperty, "Surface");
        return row;
    }

    static void RoundEnds(Panel list)
    {
        var rows = list.Children.OfType<FrameworkElement>().Where(r => r.Visibility == Visibility.Visible).ToList();
        foreach (var r in rows) r.SetValue(Border.CornerRadiusProperty, new CornerRadius(r == rows[0] ? 16 : 0, r == rows[0] ? 16 : 0, r == rows[^1] ? 16 : 0, r == rows[^1] ? 16 : 0));
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
            Heading("Tracking", 23), useCard, Face(eyes), Face(Card(OutputOptions())), Card(HeadsetHero.Controllers(96), new ThumbrestSettings(session)),
            Card(Heading("Startup"), Check("setupOnStart", "Set up the headset on Start", true), Face(installModule),
                Face(Check("startWithVrcft", "Start with VRCFaceTracking", true)), Check("openVrApps", "Open VR apps on Start", true)),
            Face(HeadsetModelOptions()), Face(ProcessingOptions()), Face(FaceCalibrationOptions), Face(testerCard), LogView
        })
            Page.Children.Add(element);
        UninstallOptions();
        ShowFace(session.Use);
        if (revealLog is not null) Dispatcher.BeginInvoke(() => PageScroll.ScrollToVerticalOffset(LogView.TranslatePoint(new Point(), Page).Y), DispatcherPriority.Loaded);
        revealLog = null;
    }

    UIElement HeadsetModelOptions()
    {
        var option = new CheckBox { Content = "Track on the headset (experimental)", IsChecked = HeadsetModelExperiment.Enabled(session) };
        AutomationProperties.SetHelpText(option, "Tracks your face, pupils and calibration in QFT+ Headset on the headset instead of on this PC. This PC starts, stops and sets it up, and VRCFaceTracking receives its expressions.");
        var status = Muted("", live: true);
        var retry = AsyncButton("Retry headset cleanup", async () => await Change(false));
        var update = AsyncButton("Update QFT+ Headset", async () => { await Change(true); session.AppProblem = ""; });
        var working = new ProgressBar { IsIndeterminate = true, Margin = new(0, 4, 0, 12) };
        var cancel = Button("Cancel", session.CancelSetup);
        var changing = false;
        async Task Change(bool enabled)
        {
            if (busy || recording || training) return;
            busy = changing = true;
            StartButton.Content = "Cancel";
            ClearNotice();
            trackingRefresh?.Invoke();
            try { await session.SetHeadsetModel(enabled); }
            catch (OperationCanceledException) { session.Notify("Ready"); }
            catch (Exception error) { Error(error.Message); session.Notify("Headset experiment needs attention", error.Message); }
            finally
            {
                busy = changing = false;
                option.IsChecked = HeadsetModelExperiment.Enabled(session);
                UseChanged(session.Use);
                SidebarStatus();
                trackingRefresh?.Invoke();
            }
        }
        option.Click += async (_, _) => await Change(option.IsChecked == true);
        void Refresh()
        {
            option.IsEnabled = retry.IsEnabled = update.IsEnabled = !busy && !recording && !training && !session.Running;
            retry.Visibility = Visible(!changing && HeadsetModelExperiment.Pending(session));
            update.Visibility = Visible(HeadsetModelExperiment.Standalone(session) && session.AppProblem == HeadsetApp.Outdated && !changing);
            working.Visibility = cancel.Visibility = Visible(changing);
            status.Text = changing ? session.State + ". " + session.Detail : HeadsetModelExperiment.Pending(session) ? "Off, but QFT+ Headset is still on the headset. Connect the headset, then choose Retry to remove it."
                : update.Visibility == Visibility.Visible ? HeadsetApp.Outdated
                : HeadsetModelExperiment.Enabled(session) ? "On. QFT+ Headset is paired with this PC; Start and Stop here control it." : "Off. Tracking runs on this PC.";
        }
        trackingRefresh += Refresh;
        Refresh();
        return Card(option, Muted("Turning it on installs QFT+ Headset and pairs it with this PC; allow its root request in the headset the first time. Turning it off removes the app and its calibration. Accuracy and headset load are still being tested."), status, working, Buttons(retry, update, cancel));
    }

    StackPanel OutputOptions()
    {
        var rows = new StackPanel();
        if (HeadsetModelExperiment.Standalone(session)) return HeadsetAppOptions();
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

    async Task SendToHeadset(string key, object value)
    {
        try { (session.App, session.AppProblem) = (await HeadsetApp.SendAsync(session, CancellationToken.None, (key, value)), ""); }
        catch (IOException error) { Error(error.Message); }
    }

    StackPanel HeadsetAppOptions()
    {
        var sending = 0;
        Action refresh = () => { };
        var status = Muted("", live: true);
        var rows = new StackPanel();
        rows.Children.Add(Muted("Saved in QFT+ Headset. Changes here and in the headset stay in step. Eye gaze uses Meta’s standard calibration in this experiment."));
        async Task Apply(string key, object value)
        {
            sending++;
            try { await SendToHeadset(key, value); }
            finally { sending--; refresh(); }
        }
        var output = new ComboBox { ItemsSource = HeadsetApp.Outputs.Select(o => o.Title).ToList() };
        AutomationProperties.SetName(output, "Send tracking to");
        output.SelectionChanged += async (_, _) =>
        {
            var id = HeadsetApp.Outputs[Math.Max(0, output.SelectedIndex)].Id;
            if (sending == 0 && session.App?["settings"]?["output"]?.GetValue<string>() is { } current && current != id && output.IsDropDownOpen | output.IsKeyboardFocusWithin)
                await Apply("output", id);
        };
        var destination = Muted("", live: true);
        foreach (var element in new UIElement[] { Text("Send tracking to"), output, destination })
            rows.Children.Add(element);
        var switches = new[] { ("tongue", "Tongue direction", "Uses your face calibration.", "face"),
            ("pupils", "Pupil dilation", "Uses your pupil calibration.", "pupils"),
            ("boot", "Resume after restart", "Turns tracking back on if it was on when the headset restarted.", "") }
            .Select(row =>
            {
                var box = new CheckBox { Content = row.Item2 };
                AutomationProperties.SetHelpText(box, row.Item3);
                box.Click += async (_, _) => await Apply(row.Item1, box.IsChecked == true);
                rows.Children.Add(box);
                return (Key: row.Item1, Box: box, Title: row.Item2, Needs: row.Item4);
            }).ToList();
        var rate = new ComboBox { ItemsSource = HeadsetApp.RateNames };
        AutomationProperties.SetName(rate, "Update rate");
        rate.SelectionChanged += async (_, _) =>
        {
            var value = HeadsetApp.Rates[Math.Max(0, rate.SelectedIndex)];
            if (sending == 0 && session.App?["settings"]?["rate"] is { } rate0 && rate0.GetValue<int>() != value && rate.IsDropDownOpen | rate.IsKeyboardFocusWithin)
                await Apply("rate", value);
        };
        foreach (var element in new UIElement[] { Text("Update rate"), rate,
                     Muted("How often your face is measured and sent. Faster rates follow your face more closely; slower rates use less battery and keep the headset cooler. Changing it restarts tracking."), status })
            rows.Children.Add(element);
        void Refresh()
        {
            if (sending > 0) return;
            var app = session.App;
            foreach (var (key, box, title, needs) in switches)
            {
                var allowed = needs.Length == 0 || HeadsetApp.Flag(app, "calibrated", needs);
                box.IsEnabled = app is not null && allowed;
                box.IsChecked = HeadsetApp.Flag(app, "settings", key);
                box.Content = allowed || app is null ? title : title + " · Calibrate in QFT+ Headset first";
            }
            rate.IsEnabled = app is not null;
            rate.SelectedIndex = Math.Max(0, Array.IndexOf(HeadsetApp.Rates, app?["settings"]?["rate"]?.GetValue<int>() ?? 0));
            var mode = app?["settings"]?["output"]?.GetValue<string>() ?? "";
            output.IsEnabled = app is not null;
            output.SelectedIndex = Array.FindIndex(HeadsetApp.Outputs, o => o.Id == mode);
            destination.Text = HeadsetApp.Destination(app);
            destination.Visibility = Visible(destination.Text.Length > 0);
            status.Text = session.AppProblem.Length > 0 ? session.AppProblem : app is null ? "Reading settings from the headset…" : "";
            status.Visibility = Visible(status.Text.Length > 0);
        }
        refresh = Refresh;
        trackingRefresh += Refresh;
        Refresh();
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
