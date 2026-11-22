using System.Windows;
using System.Windows.Automation;

namespace QFTPlus;

public partial class StudioWindow
{
    UIElement EyeTrackingRepairOptions()
    {
        var working = false;
        string? checkedTarget = null;
        var status = Muted(Loc.Get("EyeTrackingRepairConnect"), live: true);
        Wpf.Ui.Controls.Button repair = null!;
        void Refresh()
        {
            var target = session.Config["adbTarget"]?.GetValue<string>();
            repair.IsEnabled = !working && !busy && !recording && !training && !string.IsNullOrWhiteSpace(target);
            if (!working && checkedTarget != target) status.Text = Loc.Get("EyeTrackingRepairAbout");
            if (!working && string.IsNullOrWhiteSpace(target)) status.Text = Loc.Get("EyeTrackingRepairConnect");
        }
        repair = AsyncButton(Loc.Get("EyeTrackingRepairNow"), async () =>
        {
            if (working) return;
            working = true;
            checkedTarget = session.Config["adbTarget"]?.GetValue<string>() ?? "";
            status.Text = Loc.Get("EyeTrackingRepairWorking");
            Refresh();
            try
            {
                await EyeTrackingRepair.EnableAsync(session, checkedTarget);
                status.Text = Loc.Get("EyeTrackingRepairDone");
            }
            catch (Exception error) { status.Text = error.Message; }
            finally { working = false; Refresh(); }
        });
        AutomationProperties.SetHelpText(repair, Loc.Get("EyeTrackingRepairAbout"));
        trackingRefresh += Refresh;
        Refresh();
        return Card(Heading(Loc.Get("EyeTrackingRepairHeading")), status, repair);
    }
}
