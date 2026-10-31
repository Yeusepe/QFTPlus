using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace QFTPlus;

public partial class StudioWindow
{
    UIElement MicrophoneNoiseOptions()
    {
        var working = false;
        var dirty = false;
        string? checkedTarget = null;
        var noise = new CheckBox { Content = Loc.Get("MicrophoneNoiseOff"), IsEnabled = false, IsChecked = null };
        var gate = new CheckBox { Content = Loc.Get("MicrophoneGateAdjust"), IsEnabled = false, IsChecked = null };
        foreach (var option in new[] { noise, gate })
            AutomationProperties.SetName(option, option.Content.ToString());
        AutomationProperties.SetHelpText(gate, Loc.Get("MicrophoneGateAdjustAbout"));
        var status = Muted(Loc.Get("MicrophoneRepairConnect"), live: true);
        Wpf.Ui.Controls.Button check = null!;
        var thresholdPanel = new StackPanel();
        Wpf.Ui.Controls.Button apply = null!;
        var threshold = NumericSetting.AddTo(thresholdPanel, Loc.Get("MicrophoneGateThreshold"), 0, 100, 0,
            _ => { if (!working) Changed(); }, decimals: 1);
        threshold.TickFrequency = 0.1;
        threshold.IsSnapToTickEnabled = true;
        thresholdPanel.Children.Add(Muted(Loc.Get("MicrophoneGateThresholdAbout")));
        check = AsyncButton(Loc.Get("MicrophoneSettingsCheck"), () => Run(false));
        apply = AsyncButton(Loc.Get("MicrophoneSettingsApply"), () => Run(true));
        void Changed()
        {
            dirty = true;
            status.Text = Loc.Get("MicrophoneSettingsUnsaved");
            Refresh();
        }
        void Refresh()
        {
            var sameTarget = checkedTarget == session.Config["adbTarget"]?.GetValue<string>();
            noise.IsEnabled = gate.IsEnabled = !working && !busy && !recording && !training && sameTarget && noise.IsChecked.HasValue;
            thresholdPanel.IsEnabled = gate.IsEnabled && gate.IsChecked == true;
            apply.IsEnabled = noise.IsEnabled && dirty;
            check.IsEnabled = !working && !busy && !recording && !training;
            if (!sameTarget && !working)
            {
                noise.IsChecked = gate.IsChecked = null;
                dirty = false;
                status.Text = Loc.Get("MicrophoneRepairConnect");
            }
        }
        async Task Run(bool apply)
        {
            if (working) return;
            working = true;
            var target = session.Config["adbTarget"]?.GetValue<string>() ?? "";
            checkedTarget = target;
            status.Text = Loc.Get("CheckingYourHeadset");
            Refresh();
            try
            {
                var state = await MicrophoneSettings.RunAsync(session, target, apply ? noise.IsChecked == true : null, gate.IsChecked == true ? (int)Math.Round(threshold.Value * MicrophoneSettings.GateMaximum / 100) : null);
                noise.IsChecked = state.NoiseOff;
                gate.IsChecked = state.GateThreshold.HasValue;
                threshold.Value = (state.GateThreshold ?? 0) * 100.0 / MicrophoneSettings.GateMaximum;
                dirty = false;
                status.Text = Loc.Get(state.Message);
            }
            catch (Exception error)
            {
                noise.IsChecked = gate.IsChecked = null;
                dirty = false;
                status.Text = error.Message;
            }
            finally { working = false; Refresh(); }
        }
        noise.Click += (_, _) => Changed();
        gate.Click += (_, _) => Changed();
        trackingRefresh += Refresh;
        var panel = new StackPanel();
        foreach (var element in new UIElement[] { noise, gate, thresholdPanel, status, Buttons(apply, check) }) panel.Children.Add(element);
        Refresh();
        if (!preview) _ = Run(false);
        return Card(Heading(Loc.Get("MicrophoneSettingsHeading")), panel);
    }
}
