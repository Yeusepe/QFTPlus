using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Ui = Wpf.Ui.Controls;

namespace QFTPlus;

public partial class StudioWindow
{
    internal Action? onboardingRefresh;
    internal int onboardingStep = -1;
    int onboardingView, onboardingShown = -1;
    bool onboardingBack;
    Action? onboardingBackAction, onboardingSecondaryAction;
    HeadsetHero? journey;
    Grid? journeyStage;
    const int StepCount = 5;

    sealed class Spring(double response, double damping, double settle) : EasingFunctionBase
    {
        internal double Seconds => settle;
        protected override double EaseInCore(double t)
        {
            var omega = 2 * Math.PI / response;
            var time = t * settle;
            if (damping == 1) return t >= 1 ? 1 : 1 - Math.Exp(-omega * time) * (1 + omega * time);
            var damped = omega * Math.Sqrt(1 - damping * damping);
            return t >= 1 ? 1 : 1 - Math.Exp(-damping * omega * time) * (Math.Cos(damped * time) + damping * omega / damped * Math.Sin(damped * time));
        }
        protected override Freezable CreateInstanceCore() => new Spring(response, damping, settle);
    }
    static readonly Spring Move = new(.6, .82, .77) { EasingMode = EasingMode.EaseIn }, Fade = new(.9, .92, .91) { EasingMode = EasingMode.EaseIn };

    static void Animate(IAnimatable target, DependencyProperty property, double from, double to, Spring ease, EventHandler? done = null)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(SystemParameters.ClientAreaAnimation ? ease.Seconds : 0)) { EasingFunction = ease };
        if (done is not null) animation.Completed += done;
        target.BeginAnimation(property, animation);
    }

    static (ScaleTransform Scale, TranslateTransform Shift) Transforms(UIElement element)
    {
        if (element.RenderTransform is TransformGroup { Children: [ScaleTransform scale, TranslateTransform shift] }) return (scale, shift);
        var group = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };
        (element.RenderTransform, element.RenderTransformOrigin) = (group, new(.5, .5));
        return ((ScaleTransform)group.Children[0], (TranslateTransform)group.Children[1]);
    }

    internal void ShowOnboarding() => Onboard(0);

    void OnboardingBackClick(object sender, RoutedEventArgs e) => onboardingBackAction?.Invoke();

    void OnboardingSecondaryClick(object sender, RoutedEventArgs e) => onboardingSecondaryAction?.Invoke();

    internal void CloseOnboarding()
    {
        if (onboardingStep < 0) return;
        if (busy && session.SettingUp) session.CancelSetup();
        session.Save("onboarded", true);
        (onboardingStep, onboardingShown, onboardingRefresh, journey, journeyStage) = (-1, -1, null, null, null);
        var view = ++onboardingView;
        Onboarding.IsHitTestVisible = false;
        var quick = new Spring(.35, 1, .25) { EasingMode = EasingMode.EaseIn };
        var (scale, shift) = Transforms(OnboardingCard);
        Animate(scale, ScaleTransform.ScaleXProperty, scale.ScaleX, .96, quick);
        Animate(scale, ScaleTransform.ScaleYProperty, scale.ScaleY, .96, quick);
        Animate(shift, TranslateTransform.YProperty, shift.Y, 16, quick);
        Animate(OnboardingCard, OpacityProperty, OnboardingCard.Opacity, 0, quick);
        foreach (var blur in Shell.Children.OfType<UIElement>().Select(c => c.Effect).OfType<BlurEffect>()) Animate(blur, BlurEffect.RadiusProperty, blur.Radius, 0, quick);
        Animate(OnboardingScrim, OpacityProperty, OnboardingScrim.Opacity, 0, quick, (_, _) =>
        {
            if (onboardingView != view) return;
            Onboarding.Visibility = Visibility.Collapsed;
            foreach (UIElement child in Shell.Children) child.Effect = null;
        });
        SidebarStatus();
        trackingRefresh?.Invoke();
    }

    internal void Onboard(int step)
    {
        var opening = onboardingShown < 0;
        var before = opening ? 0 : OnboardingCard.ActualHeight;
        var forward = !onboardingBack || opening;
        onboardingBack = false;
        (onboardingStep, onboardingShown) = (step, step);
        var view = ++onboardingView;
        bool Here() => onboardingView == view && Onboarding.IsVisible;
        void Go(int next)
        {
            if (!Here()) return;
            onboardingBack = next < step;
            Onboard(next);
        }
        (Onboarding.Visibility, Onboarding.IsHitTestVisible) = (Visibility.Visible, true);
        if (opening)
        {
            var (scale, shift) = Transforms(OnboardingCard);
            Animate(OnboardingScrim, OpacityProperty, 0, 1, Fade);
            Animate(OnboardingCard, OpacityProperty, 0, 1, Fade);
            Animate(scale, ScaleTransform.ScaleXProperty, .94, 1, Move);
            Animate(scale, ScaleTransform.ScaleYProperty, .94, 1, Move);
            Animate(shift, TranslateTransform.YProperty, 28, 0, Move);
            foreach (UIElement child in Shell.Children)
                if (child != Onboarding && child is not Ui.TitleBar)
                {
                    var blur = new BlurEffect { Radius = 8 };
                    child.Effect = blur;
                    Animate(blur, BlurEffect.RadiusProperty, 0, 8, Fade);
                }
        }
        else Animate(OnboardingContent, OpacityProperty, 0, 1, Fade);
        OnboardingSteps.Children.Clear();
        for (var i = 0; i < StepCount; i++)
        {
            var track = new Border { CornerRadius = new(2), Opacity = .2 };
            track.SetResourceReference(Border.BackgroundProperty, "Muted");
            var fill = new Border { CornerRadius = new(2), RenderTransformOrigin = new(0, .5), RenderTransform = new ScaleTransform(i <= step ? 1 : 0, 1) };
            fill.SetResourceReference(Border.BackgroundProperty, "Ink");
            if (i == step && forward && !opening) Animate((ScaleTransform)fill.RenderTransform, ScaleTransform.ScaleXProperty, 0, 1, Move);
            OnboardingSteps.Children.Add(new Grid { Margin = new(3, 0, 3, 0), Children = { track, fill } });
        }
        AutomationProperties.SetName(OnboardingSteps, Loc.Format("StepOfSteps", step + 1, StepCount));
        onboardingRefresh = null;
        OnboardingBack.Icon = Symbols.Icon("chevron_left", (Brush)FindResource("Ink"), 22);
        OnboardingCard.MaxHeight = Math.Max(420, ((FrameworkElement)Content).ActualHeight - 56);
        void Back(Action? action) => (OnboardingBack.Visibility, onboardingBackAction) = (Visible(action is not null), action);
        void Secondary(string title, Action action) => (OnboardingSecondary.Content, onboardingSecondaryAction) = (title, action);
        Ui.Button Primary(string title, Action action)
        {
            var button = Button(title, action, true);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            OnboardingPrimary.Content = button;
            return button;
        }
        Back(step is > 0 and < StepCount - 1 ? () => Go(step - 1) : null);
        Secondary(Loc.Get("SetUpLater"), CloseOnboarding);

        journey ??= new HeadsetHero(200) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Hands = true, Journey = true };
        journeyStage ??= Stage(journey);
        void Hero(FrameworkElement art)
        {
            if (OnboardingHero.Child == art) return;
            OnboardingHero.Child = art;
            if (!opening) Animate(art, OpacityProperty, 0, 1, Fade);
        }
        Hero(journeyStage);
        journey.Travel(step switch { 0 => 0, 1 or 2 => 1, 3 => 2, _ => 3 });
        var wash = journeyStage.Children[0];
        Animate(wash, OpacityProperty, wash.Opacity, step == StepCount - 1 ? 1 : 0, Fade);
        void Show(string title, string about, UIElement? body = null)
        {
            (OnboardingTitle.Text, OnboardingAbout.Text, OnboardingBody.Content) = (title, about, body);
            AutomationProperties.SetName(OnboardingCard, title);
        }
        SetupService.Headset? Connected() => headsets.Where(q => q.State == "device")
            .OrderBy(q => q.Serial == (session.Config["headsetSerial"]?.GetValue<string>() ?? "") ? 0 : 1).ThenBy(q => q.Wireless ? 1 : 0).FirstOrDefault();

        switch (step)
        {
            case 0:
            {
                var about = Muted("");
                (about.TextAlignment, about.Margin) = (TextAlignment.Center, new(0, 16, 0, 0));
                var bubbles = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var (id, title, detail) in Uses)
                {
                    var allowed = !busy && !session.Running && !(SteamVr.IsSteamLink && id == "hands");
                    var label = Loc.Get(id == "face" ? "UseFaceShort" : id == "hands" ? "UseHandsShort" : "Both");
                    var bubble = new RadioButton { GroupName = "onboardingUse", Tag = label, IsChecked = session.Use == id, IsEnabled = allowed };
                    bubble.SetResourceReference(StyleProperty, "Bubble");
                    bubble.Content = Symbols.Shape(id == "face" ? "face" : id == "hands" ? "front_hand" : "head_mounted_device", 30, bubble);
                    AutomationProperties.SetHelpText(bubble, detail);
                    void Describe() => about.Text = SteamVr.IsSteamLink && id == "hands" ? Loc.Get("RequiresVirtualDesktopSteamLinkUsesItsOwnHand") : detail;
                    bubble.Checked += (_, _) =>
                    {
                        session.Save("use", id);
                        UseChanged(id);
                        Describe();
                        var pop = Transforms(bubble).Scale;
                        Animate(pop, ScaleTransform.ScaleXProperty, .9, 1, Move);
                        Animate(pop, ScaleTransform.ScaleYProperty, .9, 1, Move);
                    };
                    if (session.Use == id) Describe();
                    bubbles.Children.Add(bubble);
                }
                Show(Loc.Get("SetUpQFT"), Loc.Get("OnboardingWelcomeAbout"), Stack(bubbles, about));
                Primary(Loc.Get("Continue"), () => Go(1));
                break;
            }
            case 1:
            {
                var status = Muted("", live: true);
                status.TextAlignment = TextAlignment.Center;
                var tips = Stack([.. new[] { "UseAUSBCableThatCarriesDataChargeOnly", "InTheHeadsetAllowUSBDebuggingAndChooseAlways", "InMagiskSuperuserAllowShell" }
                    .Select(tip => { var line = Muted("• " + Loc.Get(tip)); line.Margin = new(0, 0, 0, 4); return (UIElement)line; })]);
                tips.Visibility = Visibility.Collapsed;
                var help = new Ui.Button { Content = Loc.Get("CanTFindYourHeadset"), Appearance = Ui.ControlAppearance.Transparent, BorderThickness = new(0), HorizontalAlignment = HorizontalAlignment.Center };
                help.Click += (_, _) => (tips.Visibility, help.Visibility) = (Visibility.Visible, Visibility.Collapsed);
                Show(Loc.Get("ConnectYourQuestPro"), Loc.Get("OnboardingConnectAbout"), Stack(status, help, tips));
                var next = Primary(Loc.Get("Continue"), () => Go(2));
                void Refresh()
                {
                    var waiting = headsets.FirstOrDefault(q => q.State != "device");
                    status.Text = Connected() is { } found ? Loc.Format("ConnectedBySerial", found.Wireless ? Loc.Format("WiFi", found.Target) : Loc.Get("UsbConnection"), found.Serial)
                        : waiting?.State == "unauthorized" ? Loc.Get("InTheHeadsetChooseAlwaysAllowFromThisComputer")
                        : waiting is not null ? Loc.Get("RestartTheHeadsetAndKeepUSBConnectedThenSearch") : Loc.Get("SearchingUSBAndWiFi");
                    next.IsEnabled = Connected() is not null;
                }
                Refresh();
                _ = Watch();
                async Task Watch()
                {
                    while (Here() && !preview)
                    {
                        if (!searching)
                        {
                            searching = true;
                            try { (headsets, headsetsChecked) = (await session.Discover(), DateTime.Now); }
                            catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException) { }
                            finally { searching = false; }
                        }
                        if (!Here()) return;
                        Refresh();
                        await Task.Delay(2500);
                    }
                }
                break;
            }
            case 2:
            {
                var wanted = new List<(string Key, string Title, string Icon, string Place)>();
                if (session.Use != "hands") wanted.AddRange([("ft_enabled", Loc.Get("NaturalFacialExpressions"), "face", "face_tracking_turn_on"),
                    ("et_enabled", Loc.Get("EyeTracking"), "eye_tracking", "eye_tracking_turn_on")]);
                if (session.Use != "face") wanted.AddRange([("hand_tracking_enabled", Loc.Get("HandAndBodyTracking"), "front_hand", "hands_hand_tracking"),
                    ("hybrid", Loc.Get("HandsAndControllersTogether"), "tune", SetupService.Singularity)]);
                var service = new SetupService(session.Root);
                var headset = Connected();
                var cluster = Cluster(wanted.Select(w => (w.Title, w.Icon)).ToArray());
                cluster.Show(new bool?[wanted.Count]);
                Hero(cluster.View);
                Show(Loc.Get("GettingYourHeadsetReady"), headset is null ? Loc.Get("ConnectYourQuestProFirst") : Loc.Get("CheckingYourHeadset"));
                (string Key, string Title, string Icon, string Place)? missing = null;
                var action = Primary(Loc.Get("Continue"), () => { if (missing is { } item) _ = Guide(item); else Go(3); });
                bool? State(string output, string key) => key == "hybrid"
                    ? SetupService.Feature(output, "hand_tracking_enabled") is not { } hands ? null : hands && SetupService.HandSettingsProblem(output) is null
                    : SetupService.Feature(output, key);
                async Task Guide((string Key, string Title, string Icon, string Place) item)
                {
                    if (headset is null) return;
                    try
                    {
                        await service.OpenInHeadsetAsync(headset.Target, item.Place, CancellationToken.None);
                        OnboardingAbout.Text = Loc.Format("SwitchOnInYourHeadset", item.Title);
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException) { OnboardingAbout.Text = Loc.Format("CouldnTOpenHeadsetSettings", error.Message); }
                }
                if (headset is null || preview) break;
                _ = Watch();
                async Task Watch()
                {
                    string? pointed = null;
                    while (Here())
                    {
                        string output;
                        try { output = await service.HeadsetFeaturesAsync(headset.Target, CancellationToken.None); }
                        catch (Exception error) when (error is IOException or InvalidOperationException) { output = ""; }
                        if (!Here()) return;
                        var states = wanted.Select(w => State(output, w.Key)).ToArray();
                        cluster.Show(states);
                        var next = wanted.Select((w, i) => (w, i)).FirstOrDefault(x => states[x.i] == false);
                        missing = next.w.Key is null ? null : next.w;
                        if (states.All(s => s == true))
                        {
                            (OnboardingTitle.Text, OnboardingAbout.Text, action.Content) = (Loc.Get("YourHeadsetIsReady"), Loc.Get("EverythingQFTNeedsIsOn"), Loc.Get("Continue"));
                        }
                        else if (missing is { } item)
                        {
                            OnboardingTitle.Text = Loc.Format("TurnOnItem", item.Title);
                            if (pointed != item.Key) OnboardingAbout.Text = item.Key == "hybrid" ? SetupService.HandSettingsProblem(output) ?? "" : Loc.Get("QFTOpensTheRightSettingInYourHeadset");
                            action.Content = Loc.Get("ShowInHeadset");
                            if (forward && pointed != item.Key)
                            {
                                pointed = item.Key;
                                await Guide(item);
                            }
                        }
                        else OnboardingAbout.Text = Loc.Get("InMagiskSuperuserAllowShellAccess");
                        await Task.Delay(1500);
                    }
                }
                break;
            }
            case 3:
            {
                var detail = Muted("", live: true);
                detail.TextAlignment = TextAlignment.Center;
                var bar = new ProgressBar { Margin = new(0, 4, 0, 12) };
                bar.SetResourceReference(ForegroundProperty, "Ink");
                var fixes = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
                Show(Loc.Get("FinishSetup"), Loc.Get("OnboardingFinishAbout"), Stack(bar, detail, fixes));
                var action = Primary("", () => { if (busy) session.CancelSetup(); else if (setupDone) Go(4); else _ = PrepareHeadset(); });
                onboardingRefresh = () =>
                {
                    if (!Here()) return;
                    var failed = !busy && setupProblem.Length > 0;
                    bar.Visibility = Visible(busy);
                    bar.Value = session.Progress;
                    OnboardingAbout.Text = busy && setupStep >= 0 && setupStep < SetupSteps.Length
                        ? Loc.Format("StepOf", setupStep + 1, SetupSteps.Length, SetupSteps[setupStep].Title) : Loc.Get("OnboardingFinishAbout");
                    detail.Text = failed ? setupProblem : busy ? session.Detail : "";
                    detail.Visibility = Visible(detail.Text.Length > 0);
                    fixes.Children.Clear();
                    if (failed && moduleMissing) fixes.Children.Add(Button(Loc.Get("InstallModule"), () => { session.Save("installModule", true); _ = PrepareHeadset(); }));
                    else if (failed && session.HelpTarget is { } help) fixes.Children.Add(Button(session.HelpCaption, () => Open(help)));
                    action.Content = busy ? Loc.Get("Cancel") : setupDone ? Loc.Get("Continue") : failed ? Loc.Get("TryAgain") : Loc.Get("SetUpAction");
                    if (setupDone && !busy) (OnboardingTitle.Text, OnboardingAbout.Text) = (Loc.Get("SetupFinished"), Loc.Get("EverythingIsInstalled"));
                    OnboardingBack.IsEnabled = !busy;
                };
                onboardingRefresh();
                if (forward && !preview && !busy && !setupDone) _ = PrepareHeadset();
                break;
            }
            default:
            {
                Show(Loc.Get("YouReAllSet"), Loc.Get("OnboardingReadyAbout"));
                Primary(Loc.Get("StartTracking"), () => { CloseOnboarding(); _ = Start(); });
                Secondary(Loc.Get("Done"), CloseOnboarding);
                break;
            }
        }
        if (before > 0)
        {
            OnboardingCard.BeginAnimation(HeightProperty, null);
            OnboardingCard.Height = double.NaN;
            OnboardingCard.UpdateLayout();
            Animate(OnboardingCard, HeightProperty, before, OnboardingCard.ActualHeight, Move, (_, _) =>
            {
                if (onboardingView != view) return;
                OnboardingCard.BeginAnimation(HeightProperty, null);
                OnboardingCard.Height = double.NaN;
            });
        }
        Dispatcher.BeginInvoke(() => (OnboardingPrimary.Content as UIElement)?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    internal static Grid Stage(FrameworkElement art)
    {
        var stage = new Grid();
        stage.SizeChanged += (_, e) => stage.Clip = new CombinedGeometry(GeometryCombineMode.Intersect,
            new RectangleGeometry(new(0, 0, e.NewSize.Width, e.NewSize.Height + 23), 23, 23), new RectangleGeometry(new(e.NewSize)));
        stage.Children.Add(Wash());
        stage.Children.Add(art);
        return stage;
    }

    internal (FrameworkElement View, Action<bool?[]> Show) Cluster((string Title, string Icon)[] items)
    {
        // Circle-packed with a 4 px gap, centred as a group.
        (double X, double Y, double Size)[] spots = items.Length switch
        {
            1 => [(0, 0, 96)],
            2 => [(-37, 6, 88), (46, -6, 70)],
            _ => [(-44, -22, 84), (34, -22, 64), (4, 35, 58), (61, 32, 50)]
        };
        var canvas = new Grid();
        var discs = new List<(Ellipse Disc, Ellipse Ring, Grid Bubble, string Icon, double Size)>();
        for (var i = 0; i < items.Length; i++)
        {
            var (x, y, size) = spots[i];
            var bubble = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var ring = new Ellipse { StrokeThickness = 1.5, Margin = new(-3), Opacity = 0 };
            ring.SetResourceReference(Shape.StrokeProperty, "Ink");
            var disc = new Ellipse();
            disc.SetResourceReference(Shape.FillProperty, "Secondary");
            bubble.Children.Add(ring);
            bubble.Children.Add(disc);
            bubble.Children.Add(Symbols.Icon(items[i].Icon, (Brush)FindResource("Ink"), size * .42));
            AutomationProperties.SetName(bubble, items[i].Title);
            var (_, shift) = Transforms(bubble);
            (shift.X, shift.Y) = (x, y);
            canvas.Children.Add(bubble);
            discs.Add((disc, ring, bubble, items[i].Icon, size));
        }
        var shown = new bool?[items.Length];
        var first = true;
        return (canvas, states =>
        {
            var attention = Array.IndexOf(states, false);
            for (var i = 0; i < discs.Count; i++)
            {
                var (disc, ring, bubble, icon, size) = discs[i];
                var on = states[i];
                if (first || on != shown[i])
                {
                    disc.SetResourceReference(Shape.FillProperty, on == true ? "Primary" : "Secondary");
                    bubble.Children.RemoveAt(2);
                    bubble.Children.Add(Symbols.Icon(icon, (Brush)FindResource(on == true ? "OnPrimary" : "Ink"), size * .42));
                    bubble.Opacity = on is null ? .55 : 1;
                    if (on == true && !first)
                    {
                        var pop = Transforms(bubble).Scale;
                        Animate(pop, ScaleTransform.ScaleXProperty, .9, 1, Move);
                        Animate(pop, ScaleTransform.ScaleYProperty, .9, 1, Move);
                    }
                }
                shown[i] = on;
                if (i == attention && SystemParameters.ClientAreaAnimation)
                {
                    if (ring.HasAnimatedProperties) continue;
                    ring.BeginAnimation(OpacityProperty, new DoubleAnimation(.15, .7, TimeSpan.FromSeconds(1.1))
                        { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
                }
                else { ring.BeginAnimation(OpacityProperty, null); ring.Opacity = i == attention ? .6 : 0; }
            }
            first = false;
        });
    }

    // Keep the wash light in dark mode, too.
    static Grid Wash()
    {
        var wash = new Grid { Background = new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xEC)) };
        foreach (var (colour, x, y, size, drift, seconds) in new[]
        {
            ("#EBC2D8", .5, .05, 1.1, 40.0, 29.0), ("#CB92EC", .95, .45, .9, 50.0, 23.0), ("#C9E7F1", .5, 1.0, 1.1, 36.0, 37.0),
            ("#D6D0F2", .02, .5, .9, 44.0, 31.0), ("#F5F7FB", .5, .5, .6, 24.0, 27.0)
        })
        {
            var tint = (Color)ColorConverter.ConvertFromString(colour);
            var field = new Rectangle
            {
                Fill = new RadialGradientBrush(tint, Color.FromArgb(0, tint.R, tint.G, tint.B))
                    { Center = new(x, y), GradientOrigin = new(x, y), RadiusX = size * .6, RadiusY = size, MappingMode = BrushMappingMode.RelativeToBoundingBox },
                Margin = new(-60)
            };
            if (SystemParameters.ClientAreaAnimation)
            {
                var move = new TranslateTransform();
                field.RenderTransform = move;
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-drift, drift, TimeSpan.FromSeconds(seconds / 2))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            }
            wash.Children.Add(field);
        }
        return wash;
    }
}
