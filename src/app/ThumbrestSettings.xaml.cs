using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace QFTPlus;

public partial class ThumbrestSettings : UserControl
{
    readonly Session session;
    readonly Dictionary<string, JsonValue> pending = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    bool applying, driverOn;
    sealed record ModeOption(string Id, string Title, string About);

    internal ThumbrestSettings(Session session)
    {
        InitializeComponent();
        this.session = session;
        var saved = session.Config["thumbrest"] as JsonObject ?? new();
        double Get(string key, double fallback) => saved[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : fallback;
        Mode.ItemsSource = new ModeOption[]
        {
            new("native", "Trackpad", "A regular SteamVR trackpad. Games and your SteamVR bindings decide what it does."),
            new("joystick", "Joystick", "Drag from where your thumb lands to push a stick. Lift to recenter."),
            new("swipe", "Swipe", "Swipe speed pushes the stick and glides out, like scrolling on a phone."),
            new("mouse", "Mouse", "Moves the Windows cursor like a laptop touchpad. Press firmly to click.")
        };
        Mode.SelectedValue = saved["mode"]?.GetValue<string>() ?? "native";
        Mode.SelectionChanged += (_, _) => { Queue("mode", JsonValue.Create((string)Mode.SelectedValue), true); ShowMode(); };
        NumericSetting.AddTo(Angles, "Left thumbrest angle", -45, 45, Get("leftRotation", -20), v => Number("leftRotation", Math.Round(v)), "°", decimals: 0);
        NumericSetting.AddTo(Angles, "Right thumbrest angle", -45, 45, Get("rightRotation", 20), v => Number("rightRotation", Math.Round(v)), "°", decimals: 0);
        NumericSetting.AddTo(Sensitivity, "Press sensitivity", 0, 1, (0.8 - Get("forceLow", .55)) / .5, v => Number("forceLow", .8 - .5 * v), "%", 100, 0);
        NumericSetting.AddTo(Joystick, "Drag for a full push", .2, 1, Get("joystickRange", .5), v => Number("joystickRange", v), "% of the pad", 50, 0);
        NumericSetting.AddTo(Swipe, "Swipe speed", .1, 1, Get("swipeGain", .35), v => Number("swipeGain", v), "%", 100 / .35, 0);
        NumericSetting.AddTo(Swipe, "Glide", 50, 1000, Get("swipeDecayMs", 350), v => Number("swipeDecayMs", Math.Round(v)), "ms", decimals: 0);
        NumericSetting.AddTo(Mouse, "Pointer speed", 300, 3000, Get("mouseSpeed", 1200), v => Number("mouseSpeed", Math.Round(v)), "px", decimals: 0);
        Resting.IsChecked = Get("restingSize", 75) > 0;
        Straighten.IsChecked = Get("railAngle", 35) > 0;
        Reversed.IsChecked = Get("triggerSlideReversed", 0) != 0;
        driverOn = session.Config["steamvrDriver"]?.GetValue<bool>() == true;
        ShowMode();
        timer.Tick += async (_, _) => await Apply();
        Unloaded += async (_, _) => await Apply();
    }

    void ShowMode()
    {
        var mode = (string?)Mode.SelectedValue;
        Joystick.Visibility = mode == "joystick" ? Visibility.Visible : Visibility.Collapsed;
        Swipe.Visibility = mode == "swipe" ? Visibility.Visible : Visibility.Collapsed;
        Mouse.Visibility = mode == "mouse" ? Visibility.Visible : Visibility.Collapsed;
        Straighten.Visibility = mode is "joystick" or "swipe" ? Visibility.Visible : Visibility.Collapsed;
        Options.Visibility = driverOn ? Visibility.Visible : Visibility.Collapsed;
        Driver.Content = driverOn ? "Uninstall SteamVR driver" : "Install SteamVR driver";
    }

    void Number(string key, double value) => Queue(key, JsonValue.Create(Math.Round(value, 3)));
    void ToggleOption(object sender, RoutedEventArgs e)
    {
        var check = (CheckBox)sender;
        var value = (string)check.Tag switch { "restingSize" => 75.0, "railAngle" => 35.0, _ => 1.0 };
        Queue((string)check.Tag, JsonValue.Create(check.IsChecked == true ? value : 0), true);
    }

    async void Queue(string key, JsonValue value, bool now = false)
    {
        pending[key] = value;
        timer.Stop();
        if (now) await Apply();
        else timer.Start();
    }

    async Task Apply()
    {
        if (applying) return;
        timer.Stop();
        if (pending.Count == 0) return;
        var batch = new Dictionary<string, JsonValue>(pending);
        pending.Clear();
        applying = true;
        try
        {
            var live = await Task.Run(() => SteamVr.SetThumbrest(batch));
            var saved = session.Config["thumbrest"]?.DeepClone() as JsonObject ?? new();
            foreach (var (key, value) in batch) saved[key] = value.DeepClone();
            session.Save("thumbrest", saved);
            Status.Text = live ? "Applied." : "Applies when SteamVR starts.";
        }
        catch (Exception error) { Status.Text = "Couldn’t change the thumbrest. " + error.Message; }
        finally { applying = false; }
        if (pending.Count > 0) timer.Start();
    }

    async void ToggleDriver(object sender, RoutedEventArgs e)
    {
        Driver.IsEnabled = false;
        try
        {
            var on = !driverOn;
            var note = await Task.Run(() => on ? SteamVrDriver.Register(Path.Combine(session.Root, "steamvr", "qftplus")) : SteamVrDriver.Unregister());
            session.Save("steamvrDriver", on);
            driverOn = on;
            ShowMode();
            if (!on) await session.StopThumbrest();
            Status.Text = note ?? (on ? "The driver is installed. Start tracking to use it." : "The driver is removed.");
        }
        catch (Exception error) { Status.Text = "Couldn’t change the SteamVR driver. " + error.Message; }
        finally { Driver.IsEnabled = true; }
    }
}
