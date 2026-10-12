using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Qpro.GazeBridge;
using Ui = Wpf.Ui.Controls;

namespace QFTPlus;

public partial class StudioWindow
{
    sealed record Hit(string Title, string? Where, string Page, string Words = "", string? Path = null, bool Reveal = true);
    string searchFrom = "Tracking";

    void InitializeSearch()
    {
        SearchBox.TextChanged += (_, _) =>
        {
            if (SearchBox.Text.Trim().Length > 0) ShowResults();
            else if (page == "Search") Navigate(searchFrom);
        };
        SearchBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && SearchBox.Text.Length > 0) SearchBox.Clear();
            else if (e.Key == Key.Enter && Find(SearchBox.Text).FirstOrDefault() is { } first) Go(first);
            else return;
            e.Handled = true;
        };
    }

    void ClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    IEnumerable<Hit> Index()
    {
        var face = session.Use != "hands";
        var onHeadset = HeadsetModelExperiment.Standalone(session);
        yield return new(Loc.Get("Tracking"), null, "Tracking", Loc.Get("OnOffStartStopStatusSwitch"), Reveal: false);
        if (!face) yield return new(Loc.Get("FaceTracking"), Loc.Get("Features"), "Tracking");
        else
        {
            yield return new(Loc.Get("EyeGaze"), Loc.Get("Features"), "Tracking", Loc.Get("EyesIndependentStandard"));
            yield return new(Loc.Get("CheeksTongueAndBrows"), Loc.Get("Features"), "Tracking", Loc.Get("FaceCalibrationPuff"));
            yield return new(Loc.Get("PupilDilation"), Loc.Get("Features"), "Tracking", Loc.Get("EyesPupils"));
        }
        yield return new(Loc.Get("HybridHands"), Loc.Get("Features"), "Tracking", Loc.Get("HandTrackingControllersFingersVirtualDesktop"));
        if (face)
        {
            var parameters = File.Exists(Path.Combine(session.Root, "tracking-parameters.json")) ? Parameters() : [];
            yield return new(Loc.Get("AllAreas"), Loc.Get("Areas"), "Adjustments", Loc.Get("DefaultsEveryParameters"), "*", false);
            foreach (var area in OutputAdjustments.Areas.Where(a => parameters.Any(p => OutputAdjustments.Area(p) == a)))
                yield return new(AreaTitle(area), Loc.Get("Areas"), "Adjustments", area, area, false);
            foreach (var name in parameters)
                yield return new(Label(name), AreaTitle(OutputAdjustments.Area(name)), "Adjustments", name, name, false);
            foreach (var pair in parameters.Select(Pair).OfType<string>().Distinct())
                yield return new(Both(pair), AreaTitle(OutputAdjustments.Area(pair)), "Adjustments", pair + Loc.Get("TogetherLeftRightEyes"), pair, false);
            foreach (var (title, words) in new[] { (Loc.Get("HeadsetPassthrough"), Loc.Get("SourceMetaNative")), (Loc.Get("MatchLeftAndRight"), Loc.Get("AverageStrongerSidePairs")),
                         (Loc.Get("Strength"), Loc.Get("OutputGainMultiplier")), (Loc.Get("Offset"), Loc.Get("OutputShift")), (Loc.Get("DeadZone"), Loc.Get("OutputDeadzone")), (Loc.Get("Smoothing"), Loc.Get("OutputFilterJitter")) })
                yield return new(title, Loc.Get("AllAreas"), "Adjustments", words, "*");
            foreach (var (title, words) in new[] { (Loc.Get("ResponseCurve"), "gamma"), (Loc.Get("SmoothingWhenRelaxing"), Loc.Get("ReleaseFade")), (Loc.Get("InvertOutput"), "reverse") })
                yield return new(title, Loc.Get("Response"), "Adjustments", words, "*");
            yield return new(Loc.Get("ResetDefaults"), Loc.Get("AllAreas"), "Adjustments", Loc.Get("ResetRestore"), "*");
            foreach (var title in new[] { Loc.Get("InputMinimum"), Loc.Get("InputMaximum"), Loc.Get("InputNeutral"), Loc.Get("OutputMinimum"), Loc.Get("OutputMaximum") })
                yield return new(title, Loc.Get("InputAndOutputLimits"), "Adjustments", Loc.Get("RangeClampRest"), Reveal: false);
            if (parameters.Any(p => OutputAdjustments.Area(p) == "Gaze"))
            {
                yield return new(Loc.Get("Convergence"), Loc.Get("Gaze"), "Adjustments", Loc.Get("IndependentEyeDepthVergence"), "Gaze");
                yield return new(Loc.Get("ConvergenceStrength"), Loc.Get("Gaze"), "Adjustments", Loc.Get("EyesDepthGainVergence"), "Gaze");
                yield return new(Loc.Get("ResetTo100"), Loc.Get("Gaze"), "Adjustments", Loc.Get("ConvergenceStrengthDefault"), "Gaze");
            }
            yield return new(Loc.Get("OverrideLiveTracking"), null, "Manual", Loc.Get("TestMovementsManualSlidersPreviewAvatar"));
            if (onHeadset)
            {
                yield return new(Loc.Get("FaceAndTongue"), "QFT+ Headset", "Calibration", Loc.Get("CalibrateFaceExpressionsCheeks"));
                yield return new(Loc.Get("PupilDilation"), "QFT+ Headset", "Calibration", Loc.Get("CalibratePupilsEyes"));
            }
            else
            {
                yield return new(Loc.Get("FaceCalibration"), null, "Calibration", Loc.Get("CalibrateExpressionsEnrollTongueCheeks"), "enroll", false);
                yield return new(Loc.Get("PupilCalibration"), null, "Calibration", Loc.Get("CalibratePupilDilationEyes"), "pupils", false);
                yield return new(Loc.Get("MoreTimeForEachPose"), null, "Calibration", Loc.Get("SlowCalibration"));
            }
            if (!onHeadset)
            {
                yield return new(Loc.Get("ShowPupilOutlines"), null, "Cameras", Loc.Get("CameraEyes"));
                foreach (var title in new[] { Loc.Get("LeftEye"), Loc.Get("RightEye"), Loc.Get("LeftFace"), Loc.Get("RightFace"), Loc.Get("Brow") })
                    yield return new(title, Loc.Get("Cameras"), "Cameras", Loc.Get("CameraViewImage"));
            }
        }
        yield return new(Loc.Get("LipSync"), null, "LipSync", Loc.Get("LipSyncSearchWords"));
        yield return new(Loc.Get("Microphone"), Loc.Get("LipSync"), "LipSync", Loc.Get("LipSyncSearchWords"));
        foreach (var detail in new[] { "MouthShapes", "Sound", "VRChat" })
            yield return new(Loc.Get(detail), Loc.Get("LipSync"), "LipSync/" + detail, Loc.Get("LipSyncSearchWords"));
        yield return new(Loc.Get("SearchAgain"), Loc.Get("Headset"), "Setup", Loc.Get("FindUsbWiFiWifiConnectHeadset"));
        foreach (var (_, title, _) in Uses)
            yield return new(title, Loc.Get("WhatDoYouWantToUse"), "Settings", Loc.Get("UseModeFaceHandsControllersVirtualDesktop"));
        if (face)
        {
            yield return new(Loc.Get("IndependentEyeGaze"), null, "Settings", Loc.Get("EyesSeparatelyDepthConvergence"));
            if (onHeadset)
            {
                yield return new(Loc.Get("SendTrackingTo"), null, "Settings", Loc.Get("OutputDestinationOscVrchatReceiver"));
                foreach (var (title, words) in new[] { (Loc.Get("TongueDirection"), Loc.Get("FaceCalibration_bca2dbed")), (Loc.Get("PupilDilation"), Loc.Get("EyesPupils")), (Loc.Get("ResumeAfterRestart"), Loc.Get("BootStartupReboot")),
                             (Loc.Get("UpdateRate"), Loc.Get("FpsFrameRateSpeedLatencyBatteryHeat")) })
                    yield return new(title, "QFT+ Headset", "Settings", words);
            }
            else
            {
                yield return new(Loc.Get("Tongue"), null, "Settings", "output");
                yield return new(Loc.Get("ExtraExpressions"), null, "Settings", Loc.Get("OutputFaceCheeksBrows"));
                foreach (var group in CalibrationSettings.FaceGroups) yield return new(Loc.Get(group.Title), Loc.Get("ExtraExpressions"), "Settings", "output");
                yield return new(Loc.Get("PupilDilation"), null, "Settings", Loc.Get("OutputEyesPupils"));
            }
        }
        foreach (var title in new[] { Loc.Get("ThumbrestAndTrigger"), Loc.Get("ThumbrestMode"), Loc.Get("IgnoreARestingThumb"), Loc.Get("StraightenScrolling"), Loc.Get("ReverseTriggerSlide") })
            yield return new(title, Loc.Get("Controllers"), "Settings", Loc.Get("ControllersTouchThumbrestTriggerScroll"));
        yield return new(Loc.Get("SetUpTheHeadsetOnStart"), Loc.Get("Startup"), "Settings", Loc.Get("LaunchAutomatic"));
        if (face)
        {
            yield return new(Loc.Get("InstallTheVRCFaceTrackingModule"), Loc.Get("Startup"), "Settings", Loc.Get("VrcftModule"));
            yield return new(Loc.Get("StartWithVRCFaceTracking"), Loc.Get("Startup"), "Settings", Loc.Get("VrcftLaunchAutomatic"));
        }
        yield return new(Loc.Get("OpenVRAppsOnStart"), Loc.Get("Startup"), "Settings", Loc.Get("SteamvrVirtualDesktopLaunch"));
        if (face)
        {
            yield return new(Loc.Get("TrackOnTheHeadsetExperimental"), null, "Settings", Loc.Get("StandaloneQftHeadsetAppOnDevice"));
            yield return new(Loc.Get("TrackingProcessor"), Loc.Get("Processing"), "Settings", Loc.Get("GpuCpuDirectmlGraphicsCardPerformance"));
            yield return new(Loc.Get("SaveFaceTrackingLogs"), Loc.Get("FaceCalibration"), "Settings", Loc.Get("LogsDiagnostics"));
            yield return new(Loc.Get("HelpImproveTracking"), null, "Settings", Loc.Get("TesterShareRecordingsBenchmark"));
        }
        yield return new(Loc.Get("Log"), null, "Settings", Loc.Get("LogsErrorsDiagnosticsTroubleshooting"));
        yield return new(Loc.Get("CheckForUpdates"), Loc.Get("Updates"), "Settings", Loc.Get("VersionUpdateRelease"));
        yield return new(Loc.Get("UninstallQFT"), Loc.Get("Uninstall"), "Settings", Loc.Get("RemoveDelete"));
    }

    List<Hit> Find(string text)
    {
        var comparison = CultureInfo.CurrentUICulture.CompareInfo;
        bool Contains(string value, string term) => comparison.IndexOf(value, term, CompareOptions.IgnoreCase) >= 0 || value.Contains(term, StringComparison.OrdinalIgnoreCase);
        bool StartsWith(string value, string term) => comparison.IsPrefix(value, term, CompareOptions.IgnoreCase) || value.StartsWith(term, StringComparison.OrdinalIgnoreCase);
        var phrase = text.Trim();
        var terms = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];
        return Index().Select(hit =>
        {
            var title = hit.Title;
            var all = $"{title} {hit.Where} {TitleOf(hit.Page)} {hit.Words}";
            return (hit, rank: terms.Any(t => !Contains(all, t)) ? -1 : StartsWith(title, phrase) ? 0 : title.Split(' ').Any(w => StartsWith(w, terms[0])) ? 1
                : terms.All(t => Contains(title, t)) ? 2 : 3);
        }).Where(r => r.rank >= 0).OrderBy(r => r.rank).Select(r => r.hit).ToList();
    }

    void ShowResults()
    {
        if (recording)
        {
            Error(Loc.Get("FinishOrCancelCalibrationFirst"));
            return;
        }
        if (page != "Search") searchFrom = page;
        page = "Search";
        Clear(Loc.Get("SearchResults"));
        Navigation.SelectedItem = null;
        var hits = Find(SearchBox.Text);
        if (hits.Count == 0)
            Page.Children.Add(Card(Text(Loc.Format("NoResultsFor", SearchBox.Text.Trim())), Muted(Loc.Get("CheckTheSpellingOrSearchForASettingArea"))));
        foreach (var group in hits.GroupBy(h => h.Page))
        {
            var list = new StackPanel();
            foreach (var hit in group)
            {
                var row = ListRow(hit.Title, null, () => Go(hit));
                ShowRow(row, hit.Where ?? "");
                list.Children.Add(row);
            }
            RoundEnds(list);
            var header = Section(TitleOf(group.Key));
            if (Page.Children.Count == 0) header.Margin = new(0, 0, 0, 12);
            Page.Children.Add(header);
            Page.Children.Add(list);
        }
    }

    void Go(Hit hit)
    {
        if (recording) { Error(Loc.Get("FinishOrCancelCalibrationFirst")); return; }
        if (hit.Path is { } path)
        {
            if (hit.Page == "Calibration") kind = path;
            else outputParameter = path;
        }
        Navigate(hit.Page);
        if (hit.Reveal && page == hit.Page) Dispatcher.BeginInvoke(() => Reveal(hit), DispatcherPriority.Loaded);
    }

    void Reveal(Hit hit, bool opened = false)
    {
        if (!opened && Descendants<Expander>(Page).Where(e => Equals(e.Header, hit.Where) && !e.IsExpanded).ToList() is { Count: > 0 } closed)
        {
            closed.ForEach(e => e.IsExpanded = true);
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 400 : 50) };
            settle.Tick += (_, _) => { settle.Stop(); if (page == hit.Page) Reveal(hit, true); };
            settle.Start();
            return;
        }
        var texts = Descendants<TextBlock>(Page).Concat(Descendants<TextBlock>(Actions)).ToList();
        if ((texts.FirstOrDefault(t => t.Text == hit.Title) ?? texts.FirstOrDefault(t => t.Text.StartsWith(hit.Title))) is not { } text) return;
        FrameworkElement row = text;
        for (DependencyObject? at = text; at is not null && at != Page; at = VisualTreeHelper.GetParent(at))
            if (at is ButtonBase or NumericSetting or ComboBox) { row = (FrameworkElement)at; break; }
        var height = row.ActualHeight;
        if (row is TextBlock && row.Parent is Panel panel && panel.Children.IndexOf(row) + 1 is var next && next < panel.Children.Count && panel.Children[next] is Control control)
            height = control.TranslatePoint(new Point(0, control.ActualHeight), row).Y;
        var top = row.TranslatePoint(new Point(), Page).Y;
        PageScroll.ScrollToVerticalOffset(top - (PageScroll.ViewportHeight - height) / 2);
        if (AdornerLayer.GetAdornerLayer(row) is not { } layer) return;
        var ink = ((SolidColorBrush)FindResource("Ink")).Color;
        var mark = new Highlight(row, new SolidColorBrush(Color.FromArgb(0x24, ink.R, ink.G, ink.B)), row is Ui.CardAction ? 0 : 8, height);
        layer.Add(mark);
        var animated = SystemParameters.ClientAreaAnimation;
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(2.5) };
        foreach (var (opacity, at) in new[] { (0.0, 0.0), (1, .15), (1, 2.1), (0, 2.5) })
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(animated ? opacity : 1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(at)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        fade.Completed += (_, _) => layer.Remove(mark);
        mark.BeginAnimation(OpacityProperty, fade);
    }

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    sealed class Highlight : Adorner
    {
        readonly Brush fill;
        readonly double inset, height;

        internal Highlight(UIElement row, Brush fill, double inset, double height) : base(row) => (this.fill, this.inset, this.height, IsHitTestVisible) = (fill, inset, height, false);

        protected override void OnRender(DrawingContext drawing) =>
            drawing.DrawRoundedRectangle(fill, null, new Rect(-inset, -inset / 2, AdornedElement.RenderSize.Width + 2 * inset, height + inset), 12, 12);
    }
}
