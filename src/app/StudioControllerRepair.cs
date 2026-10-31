using System.Windows;
using System.Windows.Automation;

namespace QFTPlus;

public partial class StudioWindow
{
    UIElement ControllerRepairOptions()
    {
        var working = false;
        var status = Muted(Loc.Get("ControllerRepairAbout"), live: true);
        Wpf.Ui.Controls.Button repair = null!;
        void Refresh() => repair.IsEnabled = !working && !busy && !recording && !training;
        repair = AsyncButton(Loc.Get("RepairControllers"), async () =>
        {
            if (working) return;
            working = true;
            status.Text = Loc.Get("ControllerRepairWorking");
            Refresh();
            try { status.Text = Loc.Get(await ControllerRepair.RunAsync(session, session.Config["adbTarget"]?.GetValue<string>() ?? "")); }
            catch (Exception error) { status.Text = error.Message; }
            finally { working = false; Refresh(); }
        });
        AutomationProperties.SetHelpText(repair, Loc.Get("ControllerRepairAbout"));
        trackingRefresh += Refresh;
        Refresh();
        return Card(Heading(Loc.Get("ControllerRepairHeading")), status, repair);
    }
}
