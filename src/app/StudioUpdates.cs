using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace QFTPlus;
public partial class StudioWindow
{
    readonly HttpClient updateClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly CancellationTokenSource updateLifetime = new();
    ReleaseVersion? installedVersion;
    AppRelease? availableUpdate;
    string? dismissedUpdate;
    string updateStatus = "";
    bool checkingUpdates;
    DateTime lastUpdateAttempt = DateTime.MinValue;
    Action? updateRefresh;

    void InitializeUpdates()
    {
        installedVersion = Updates.Installed(session.Root);
        dismissedUpdate = session.Config["dismissedUpdate"]?.GetValue<string>();
        Activated += async (_, _) =>
        {
            if (!preview && session.Config["checkForUpdates"]?.GetValue<bool>() != false && DateTime.UtcNow - lastUpdateAttempt >= TimeSpan.FromDays(1))
                await CheckUpdates();
        };
        Closed += (_, _) => { updateLifetime.Cancel(); updateClient.Dispose(); updateLifetime.Dispose(); };
    }

    async Task CheckUpdates()
    {
        if (checkingUpdates || installedVersion is null || updateLifetime.IsCancellationRequested) return;
        checkingUpdates = true; lastUpdateAttempt = DateTime.UtcNow;
        updateStatus = "Checking for updates…"; RefreshUpdates();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            availableUpdate = await Updates.Check(updateClient, installedVersion, timeout.Token);
            updateStatus = availableUpdate is null ? "You’re up to date." : "";
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            updateStatus = "Couldn’t check for updates. Try again later, or view releases on GitHub.";
        }
        finally { checkingUpdates = false; if (!updateLifetime.IsCancellationRequested) RefreshUpdates(); }
    }

    void RefreshUpdates()
    {
        UpdateBanner.Visibility = page == "Tracking" && availableUpdate is not null && availableUpdate.Version.Text != dismissedUpdate
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateMessage.Text = availableUpdate is null ? "" : $"QFT+ {availableUpdate.Version.Text} is available. See what’s new and download it on GitHub.";
        updateRefresh?.Invoke();
    }

    void ViewUpdate(object sender, RoutedEventArgs e) => OpenUpdate();
    void OpenUpdate()
    {
        try { Open(availableUpdate?.Url ?? Updates.ReleasesUrl); }
        catch (Exception) { Error("Couldn’t open your browser. Visit github.com/Yeusepe/QFTPlus/releases to get the update."); }
    }

    void DismissUpdate(object sender, RoutedEventArgs e)
    {
        dismissedUpdate = availableUpdate?.Version.Text;
        if (!preview)
        {
            try { session.Save("dismissedUpdate", dismissedUpdate); }
            catch (Exception) { Error("The update is hidden for now. Your preference couldn’t be saved."); }
        }
        RefreshUpdates();
    }

    void UpdateOptions()
    {
        var panel = new StackPanel();
        var heading = Text("Updates", 18); heading.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2); panel.Children.Add(heading);
        panel.Children.Add(Text(installedVersion is null ? "The installed version couldn’t be read." : $"Installed version: {installedVersion.Text}", 14, true));
        var status = Text(""); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); panel.Children.Add(status);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        var check = AsyncButton("Check for updates", CheckUpdates);
        var view = Button("View releases", OpenUpdate);
        foreach (var button in new[] { check, view }) { button.Margin = new(0, 0, 8, 8); actions.Children.Add(button); }
        var automatic = new CheckBox { Content = new TextBlock { Text = "Check for updates automatically" }, IsChecked = session.Config["checkForUpdates"]?.GetValue<bool>() != false };
        automatic.Click += (_, _) => { if (!preview) session.Save("checkForUpdates", automatic.IsChecked == true); };
        panel.Children.Add(automatic);
        panel.Children.Add(Text(installedVersion?.Preview.Length > 0 ? "Includes release candidates. Updates open on GitHub." : "Updates open on GitHub.", 13, true));
        Page.Children.Add(Card(panel));
        updateRefresh = () =>
        {
            check.IsEnabled = !checkingUpdates && installedVersion is not null;
            check.Content = checkingUpdates ? "Checking…" : "Check for updates";
            view.Content = availableUpdate is null ? "View releases" : "View update";
            status.Text = updateStatus.Length > 0 ? updateStatus : availableUpdate is not null ? $"QFT+ {availableUpdate.Version.Text} is available." : "";
            status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        updateRefresh();
    }
}
