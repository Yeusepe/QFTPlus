using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace QFTPlus;

public partial class StudioWindow
{
    UIElement MicrophoneRepairOptions()
    {
        MicrophoneRepair.State? state = null;
        string? checkedTarget = null;
        var working = false;
        var option = new CheckBox { Content = Loc.Get("MicrophoneRepairOption"), IsChecked = null, IsEnabled = false };
        AutomationProperties.SetName(option, Loc.Get("MicrophoneRepairOption"));
        AutomationProperties.SetHelpText(option, Loc.Get("MicrophoneRepairAbout"));
        var status = Muted(Loc.Get("MicrophoneRepairConnect"), live: true);
        Wpf.Ui.Controls.Button check = null!;
        Wpf.Ui.Controls.Button repair = null!;
        check = AsyncButton(Loc.Get("MicrophoneRepairCheck"), () => Run());
        repair = AsyncButton(Loc.Get("MicrophoneRepairNow"), () => Run(manual: true));
        AutomationProperties.SetHelpText(repair, Loc.Get("MicrophoneRepairNowAbout"));
        void Refresh()
        {
            var sameTarget = checkedTarget == session.Config["adbTarget"]?.GetValue<string>();
            option.IsEnabled = !working && !busy && !recording && !training && sameTarget && state?.CanChange == true;
            check.IsEnabled = !working && !busy && !recording && !training;
            repair.IsEnabled = check.IsEnabled && !string.IsNullOrWhiteSpace(session.Config["adbTarget"]?.GetValue<string>());
            if (!sameTarget && !working)
            {
                option.IsChecked = null;
                status.Text = Loc.Get("MicrophoneRepairConnect");
            }
        }
        async Task Run(bool? enabled = null, bool manual = false)
        {
            if (working) return;
            working = true;
            var target = session.Config["adbTarget"]?.GetValue<string>() ?? "";
            if (checkedTarget != target) { state = null; option.IsChecked = null; }
            checkedTarget = target;
            status.Text = Loc.Get(manual ? "MicrophoneRepairNowWorking" : enabled is null ? "CheckingYourHeadset" : "MicrophoneRepairApplying");
            Refresh();
            try
            {
                if (manual)
                {
                    await MicrophoneRepair.UnmuteAsync(session, target);
                    status.Text = Loc.Get("MicrophoneRepairNowDone");
                }
                else
                {
                    state = await MicrophoneRepair.RunAsync(session, target, enabled);
                    option.IsChecked = state.Enabled;
                    status.Text = Loc.Get(state.Message);
                }
            }
            catch (Exception error)
            {
                if (!manual) { state = null; option.IsChecked = null; }
                status.Text = error.Message;
            }
            finally { working = false; Refresh(); }
        }
        option.Click += async (_, _) =>
        {
            if (checkedTarget != session.Config["adbTarget"]?.GetValue<string>()) await Run();
            else await Run(option.IsChecked == true);
        };
        trackingRefresh += Refresh;
        var card = Card(Heading(Loc.Get("MicrophoneRepairHeading")), option,
            Muted(Loc.Get("MicrophoneRepairNowAbout")), status, Buttons(repair, check));
        Refresh();
        if (!preview) _ = Run();
        return Stack(card, MicrophoneNoiseOptions());
    }
}
