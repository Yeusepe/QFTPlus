using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace QFTPlus;

internal sealed class HeadsetHero : Grid
{
    internal enum Look { Idle, Working, Connected, Warning, Error }
    static readonly string[] Clips = ["rest-connecting", "connecting-connected", "connected-rest", "connecting-rest", "rest-warning", "warning-rest", "rest-error", "error-rest"];
    static readonly Dictionary<string, WeakReference<BitmapSource[]>?> Cache = [];
    readonly Image image = new() { Stretch = Stretch.Uniform };
    readonly GradientStop core = new(Colors.Transparent, 0), rim = new(Colors.Transparent, .7);
    string variant, pose = "rest";
    Look look = Look.Idle;
    bool shown = true;
    EventHandler? playing;
    Action? next, leaving;

    internal Brush Glow { get; }

    static BitmapSource[]? Frames(string name)
    {
        if (Cache.TryGetValue(name, out var cached))
        {
            if (cached is null) return null;
            if (cached.TryGetTarget(out var kept)) return kept;
        }
        BitmapSource[]? frames;
        try
        {
            using var stream = Application.GetResourceStream(new Uri($"pack://application:,,,/QFTPlus;component/headset-render/{name}.webp")).Stream;
            frames = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames
                .Select(frame =>
                {
                    var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
                    converted.Freeze();
                    return (BitmapSource)converted;
                }).ToArray();
        }
        catch (Exception error) when (error is NotSupportedException or System.Runtime.InteropServices.COMException or IOException)
        { frames = null; }
        Cache[name] = frames is null ? null : new(frames);
        return frames;
    }

    BitmapSource Still()
    {
        var still = new BitmapImage(new Uri($"pack://application:,,,/QFTPlus;component/headset-render/{variant}-still.png"));
        still.Freeze();
        return still;
    }

    static bool Animated => SystemParameters.ClientAreaAnimation;

    internal HeadsetHero(double height, string variant = "solo")
    {
        this.variant = variant;
        Height = height; Width = height * 1.6;
        AutomationProperties.SetName(this, variant == "touch" ? "Quest Touch Pro controllers" : "Quest Pro");
        Glow = new LinearGradientBrush { StartPoint = new(.5, 1), EndPoint = new(.5, 0), GradientStops = { core, rim } };
        image.Source = Still();
        Children.Add(image);
        Loaded += (_, _) => { if (shown) Appear(); };
        Unloaded += (_, _) => { Stop(); if (!shown) Hidden(); };
    }

    void Hidden()
    {
        if (leaving is not { } then) return;
        leaving = null;
        then();
    }

    internal static HeadsetHero Controllers(double height) => new(height, "touch");

    internal bool Hands
    {
        set
        {
            var wanted = value ? "hands" : "solo";
            if (variant == "touch" || wanted == variant) return;
            if (!shown || !IsVisible || !Animated) { variant = wanted; Rest(); return; }
            Leave(() => { variant = wanted; Appear(); });
        }
    }

    internal void Present(bool show, Action? hidden = null)
    {
        if (show == shown) return;
        shown = show;
        leaving = show ? null : () => { Visibility = Visibility.Collapsed; hidden?.Invoke(); };
        if (show) { Visibility = Visibility.Visible; if (IsLoaded) Appear(); return; }
        if (!IsVisible || !Animated) { Hidden(); return; }
        Leave(() => { if (!shown) Hidden(); });
    }

    internal void Show(Look state)
    {
        if (state == look) return;
        look = state;
        if (playing is not null) { next = Go; return; }
        if (!IsVisible || !Animated) { Rest(); return; }
        Go();
    }

    string Target => look switch { Look.Working => "connecting", Look.Connected => "connected", Look.Warning => "warning", Look.Error => "error", _ => "rest" };

    void Go()
    {
        if (variant == "touch") return;
        Tint(look);
        Route(Target, null);
    }

    void Route(string target, Action? done)
    {
        if (pose == target) { Finish(done); return; }
        var direct = $"{pose}-{target}";
        var clip = Clips.Contains(direct) ? direct : pose != "rest" ? $"{pose}-rest" : target == "connected" ? "rest-connecting" : $"rest-{target}";
        var to = clip[(clip.IndexOf('-') + 1)..];
        Play(clip, () => { pose = to; Route(target, done); });
    }

    void Play(string clip, Action done)
    {
        Stop();
        var frames = Frames($"{variant}-{clip}");
        if (frames is not { Length: > 0 } || !Animated)
        {
            if (frames is { Length: > 0 }) image.Source = frames[^1];
            done();
            return;
        }
        var start = DateTime.UtcNow;
        image.Source = frames[0];
        playing = (_, _) =>
        {
            var index = (int)((DateTime.UtcNow - start).TotalSeconds * 60);
            image.Source = frames[Math.Min(index, frames.Length - 1)];
            if (index < frames.Length - 1) return;
            Stop();
            done();
        };
        CompositionTarget.Rendering += playing;
    }

    void Stop()
    {
        if (playing is not null) CompositionTarget.Rendering -= playing;
        playing = null;
    }

    void Finish(Action? done)
    {
        done?.Invoke();
        if (playing is null && next is { } then) { next = null; then(); }
    }

    void Rest()
    {
        Stop(); next = null;
        if (!shown) Hidden();
        image.OpacityMask = null;
        pose = variant == "touch" ? "rest" : Target;
        var clip = pose == "rest" ? null : pose == "connected" ? "connecting-connected" : $"rest-{pose}";
        image.Source = clip is not null && Frames($"{variant}-{clip}") is { Length: > 0 } frames ? frames[^1] : Still();
        Tint(look, animate: false);
    }

    void Appear()
    {
        Rest();
        if (!Animated || Frames($"{variant}-in") is not { Length: > 0 } frames) return;
        pose = "rest";
        Tint(Look.Idle, animate: false);
        Feather(true, frames.Length * 1000 / 60);
        Play("in", () => { if (variant != "touch" && look != Look.Idle) Go(); else Finish(null); });
    }

    void Leave(Action done)
    {
        Tint(Look.Idle);
        Route("rest", () =>
        {
            if (Frames($"{variant}-out") is not { Length: > 0 } frames) { done(); return; }
            Feather(false, frames.Length * 1000 / 60);
            Play("out", done);
        });
    }

    void Feather(bool show, int milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var edge = new GradientStop(Colors.Black, show ? -.3 : 1); var feather = new GradientStop(Colors.Transparent, show ? 0 : 1.35);
        var mask = new LinearGradientBrush { StartPoint = new(0, 1), EndPoint = new(0, 0), GradientStops = { edge, feather } };
        image.OpacityMask = mask;
        edge.BeginAnimation(GradientStop.OffsetProperty, new DoubleAnimation(show ? 1 : -.3, span) { EasingFunction = ease });
        var sweep = new DoubleAnimation(show ? 1.35 : 0, span) { EasingFunction = ease };
        if (show) sweep.Completed += (_, _) => { if (image.OpacityMask == mask) image.OpacityMask = null; };
        feather.BeginAnimation(GradientStop.OffsetProperty, sweep);
    }

    void Tint(Look state, bool animate = true)
    {
        var dark = TryFindResource("Ink") is SolidColorBrush { Color: var ink } && ink.R > 128;
        var colour = state switch
        {
            Look.Working => dark ? Color.FromRgb(0x0A, 0x84, 0xFF) : Color.FromRgb(0x00, 0x7A, 0xFF),
            Look.Connected => dark ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0x34, 0xC7, 0x59),
            Look.Warning => dark ? Color.FromRgb(0xFF, 0xB0, 0x20) : Color.FromRgb(0xFF, 0xA5, 0x00),
            Look.Error => dark ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0xFF, 0x3B, 0x30),
            _ => Color.FromArgb(0, core.Color.R, core.Color.G, core.Color.B),
        };
        var clear = Color.FromArgb(0, colour.R, colour.G, colour.B);
        if (!animate || !Animated)
        {
            core.BeginAnimation(GradientStop.ColorProperty, null); rim.BeginAnimation(GradientStop.ColorProperty, null);
            (core.Color, rim.Color) = (colour, clear);
            return;
        }
        var span = TimeSpan.FromMilliseconds(450);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        core.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(colour, span) { EasingFunction = ease });
        rim.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(clear, span) { EasingFunction = ease });
    }
}
