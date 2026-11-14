using System.Windows;
using System.Windows.Controls;
using Ui = Wpf.Ui.Controls;

namespace QFTPlus;

public partial class StudioWindow
{
    UIElement MetaGazeAveraging()
    {
        var headset = HeadsetModelExperiment.Standalone(session);
        var sending = 0;
        var syncing = false;
        var value = headset ? 25 : session.MetaGazeAveraging;
        var controls = new StackPanel { Margin = new(0, 16, 0, 0) };
        controls.Children.Add(Muted(Loc.Get("MetaGazeAveragingHelp")));
        async Task Apply()
        {
            sending++;
            try { await SendToHeadset("metaGazeAveraging", value); }
            finally { sending--; }
        }
        void Save() { if (headset) _ = Apply(); else session.SetMetaGazeAveraging(value); }
        var slider = NumericSetting.AddTo(controls, Loc.Get("EyeSamples"), 1, 25, value, n =>
        {
            if (syncing) return;
            value = (int)Math.Round(n);
            SaveLater(Save);
        }, unit:"", decimals:0);
        (slider.SmallChange, slider.LargeChange, slider.TickFrequency, slider.IsSnapToTickEnabled) = (1, 5, 1, true);
        controls.Children.Add(Button(Loc.Get("ResetMetaGazeAveraging"), () => slider.Value = 25));
        var status = Muted("", live:true); controls.Children.Add(status);
        void Refresh()
        {
            if (sending > 0) return;
            var settings = session.App?["settings"];
            controls.IsEnabled = !headset || settings is not null;
            if (headset && settings?["metaGazeAveraging"]?.GetValue<int>() is { } current && !slider.IsMouseCaptureWithin && !slider.IsKeyboardFocusWithin)
            {
                syncing = true; slider.Value = current; syncing = false;
            }
            status.Text = headset ? session.App?["metaGazeAveragingError"]?.GetValue<string>() ?? "" : session.tracking?.AveragingError ?? "";
            if (status.Text.Length == 0 && !session.Running) status.Text = Loc.Get("MetaGazeAveragingWhenTracking");
            status.Visibility = Visible(status.Text.Length > 0);
        }
        trackingRefresh += Refresh; Refresh();
        return new Ui.CardExpander { Header=Loc.Get("MetaGazeAveraging"), Content=controls, Margin=new(0,0,0,16) };
    }
}
