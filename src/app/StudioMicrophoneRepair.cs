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
        check = AsyncButton(Loc.Get("MicrophoneRepairCheck"), () => Run());
        void Refresh()
        {
            var sameTarget = checkedTarget == session.Config["adbTarget"]?.GetValue<string>();
            option.IsEnabled = !working && !busy && !recording && !training && sameTarget && state?.CanChange == true;
            check.IsEnabled = !working && !busy && !recording && !training;
            if (!sameTarget && !working)
            {
                option.IsChecked = null;
                status.Text = Loc.Get("MicrophoneRepairConnect");
            }
        }
        async Task Run(bool? enabled = null)
        {
            if (working) return;
            working = true;
            var target = session.Config["adbTarget"]?.GetValue<string>() ?? "";
            checkedTarget = target;
            status.Text = Loc.Get(enabled is null ? "CheckingYourHeadset" : "MicrophoneRepairApplying");
            Refresh();
            try
            {
                state = await MicrophoneRepair.RunAsync(session, target, enabled);
                option.IsChecked = state.Enabled;
                status.Text = Loc.Get(state.Message);
            }
            catch (Exception error)
            {
                state = null;
                option.IsChecked = null;
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
        var card = Card(Heading(Loc.Get("MicrophoneRepairHeading")), option, status, check);
        if (!preview) _ = Run();
        return card;
    }
}
