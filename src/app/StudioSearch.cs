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
        yield return new("Tracking", null, "Tracking", "on off start stop status switch", Reveal: false);
        if (!face) yield return new("Face tracking", "Features", "Tracking");
        else
        {
            yield return new("Eye gaze", "Features", "Tracking", "eyes independent standard");
            yield return new("Cheeks, tongue and brows", "Features", "Tracking", "face calibration puff");
            yield return new("Pupil dilation", "Features", "Tracking", "eyes pupils");
        }
        yield return new("Hybrid hands", "Features", "Tracking", "hand tracking controllers fingers virtual desktop");
        if (face)
        {
            var parameters = File.Exists(Path.Combine(session.Root, "tracking-parameters.json")) ? Parameters() : [];
            yield return new("All areas", "Areas", "Adjustments", "defaults every parameters", "*", false);
            foreach (var area in OutputAdjustments.Areas.Where(a => parameters.Any(p => OutputAdjustments.Area(p) == a)))
                yield return new(area, "Areas", "Adjustments", "", area, false);
            foreach (var name in parameters)
                yield return new(Label(name), OutputAdjustments.Area(name), "Adjustments", name, name, false);
            foreach (var (title, words) in new[] { ("Headset passthrough", "source meta native"), ("Match left and right", "average stronger side pairs"),
                         ("Strength", "output gain multiplier"), ("Offset", "output shift"), ("Dead zone", "output deadzone"), ("Smoothing", "output filter jitter") })
                yield return new(title, "All areas", "Adjustments", words, "*");
            foreach (var (title, words) in new[] { ("Response curve", "gamma"), ("Smoothing when relaxing", "release fade"), ("Invert output", "reverse") })
                yield return new(title, "Response", "Adjustments", words, "*");
            yield return new("Reset defaults", "All areas", "Adjustments", "reset restore", "*");
            foreach (var title in new[] { "Input minimum", "Input maximum", "Input neutral", "Output minimum", "Output maximum" })
                yield return new(title, "Input and output limits", "Adjustments", "range clamp rest", Reveal: false);
            if (parameters.Any(p => OutputAdjustments.Area(p) == "Gaze"))
            {
                yield return new("Convergence", "Gaze", "Adjustments", "independent eye depth vergence", "Gaze");
                yield return new("Convergence strength", "Gaze", "Adjustments", "eyes depth gain vergence", "Gaze");
                yield return new("Reset to 100%", "Gaze", "Adjustments", "convergence strength default", "Gaze");
            }
            yield return new("Override live tracking", null, "Manual", "test movements manual sliders preview avatar");
            if (onHeadset)
            {
                yield return new("Face and tongue", "QFT+ Headset", "Calibration", "calibrate face expressions cheeks");
                yield return new("Pupil dilation", "QFT+ Headset", "Calibration", "calibrate pupils eyes");
            }
            else
            {
                yield return new("Face calibration", null, "Calibration", "calibrate expressions enroll tongue cheeks", "enroll", false);
                yield return new("Pupil calibration", null, "Calibration", "calibrate pupil dilation eyes", "pupils", false);
                yield return new("More time for each pose", null, "Calibration", "slow calibration");
            }
            if (!onHeadset)
            {
                yield return new("Show pupil outlines", null, "Cameras", "camera eyes");
                foreach (var title in new[] { "Left eye", "Right eye", "Left face", "Right face", "Brow" })
                    yield return new(title, "Cameras", "Cameras", "camera view image");
            }
        }
        yield return new("Search again", "Headset", "Setup", "find usb wi-fi wifi connect headset");
        foreach (var (_, title, _) in Uses)
            yield return new(title, "What do you want to use?", "Settings", "use mode face hands controllers virtual desktop");
        if (face)
        {
            yield return new("Independent eye gaze", null, "Settings", "eyes separately depth convergence");
            if (onHeadset)
            {
                yield return new("Send tracking to", null, "Settings", "output destination osc vrchat receiver");
                foreach (var (title, words) in new[] { ("Tongue direction", "face calibration"), ("Pupil dilation", "eyes pupils"), ("Resume after restart", "boot startup reboot"),
                             ("Update rate", "fps frame rate speed latency battery heat") })
                    yield return new(title, "QFT+ Headset", "Settings", words);
            }
            else
            {
                yield return new("Tongue", null, "Settings", "output");
                yield return new("Extra expressions", null, "Settings", "output face cheeks brows");
                foreach (var group in CalibrationSettings.FaceGroups) yield return new(group.Title, "Extra expressions", "Settings", "output");
                yield return new("Pupil dilation", null, "Settings", "output eyes pupils");
            }
        }
        foreach (var title in new[] { "Thumbrest and trigger", "Thumbrest mode", "Ignore a resting thumb", "Straighten scrolling", "Reverse trigger slide" })
            yield return new(title, "Controllers", "Settings", "controllers touch thumbrest trigger scroll");
        yield return new("Set up the headset on Start", "Startup", "Settings", "launch automatic");
        if (face)
        {
            yield return new("Install the VRCFaceTracking module", "Startup", "Settings", "vrcft module");
            yield return new("Start with VRCFaceTracking", "Startup", "Settings", "vrcft launch automatic");
        }
        yield return new("Open VR apps on Start", "Startup", "Settings", "steamvr virtual desktop launch");
        if (face)
        {
            yield return new("Track on the headset (experimental)", null, "Settings", "standalone qft+ headset app on-device");
            yield return new("Tracking processor", "Processing", "Settings", "gpu cpu directml graphics card performance");
            yield return new("Save face-tracking logs", "Face calibration", "Settings", "logs diagnostics");
            yield return new("Help improve tracking", null, "Settings", "tester share recordings benchmark");
        }
        yield return new("Log", null, "Settings", "logs errors diagnostics troubleshooting");
        yield return new("Check for updates", "Updates", "Settings", "version update release");
        yield return new("Uninstall QFT+", "Uninstall", "Settings", "remove delete");
    }

    List<Hit> Find(string text)
    {
        var phrase = text.Trim().ToLowerInvariant();
        var terms = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];
        return Index().Select(hit =>
        {
            var title = hit.Title.ToLowerInvariant();
            var all = $"{title} {hit.Where} {TitleOf(hit.Page)} {hit.Words}".ToLowerInvariant();
            return (hit, rank: terms.Any(t => !all.Contains(t)) ? -1 : title.StartsWith(phrase) ? 0 : title.Split(' ').Any(w => w.StartsWith(terms[0])) ? 1
                : terms.All(title.Contains) ? 2 : 3);
        }).Where(r => r.rank >= 0).OrderBy(r => r.rank).Select(r => r.hit).ToList();
    }

    void ShowResults()
    {
        if (recording)
        {
            Error("Finish or cancel calibration first.");
            return;
        }
        if (page != "Search") searchFrom = page;
        page = "Search";
        Clear("Search results");
        Navigation.SelectedItem = null;
        var hits = Find(SearchBox.Text);
        if (hits.Count == 0)
            Page.Children.Add(Card(Text($"No results for “{SearchBox.Text.Trim()}”"), Muted("Check the spelling, or search for a setting, area or parameter name.")));
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
        if (recording) { Error("Finish or cancel calibration first."); return; }
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
