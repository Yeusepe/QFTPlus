using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace QFTPlus;
public partial class StudioWindow
{
    Velopack.UpdateManager? updater;
    readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(1) };
    bool installingUpdate;
    readonly CancellationTokenSource updateLifetime = new();
    Velopack.SemanticVersion? installedVersion;
    Velopack.UpdateInfo? availableUpdate;
    string? AvailableVersion => availableUpdate?.TargetFullRelease.Version.ToString();
    string? dismissedUpdate;
    string updateStatus = "";
    bool checkingUpdates;
    int updatePercent = -1;
    DateTime lastUpdateAttempt = DateTime.MinValue;
    Action? updateRefresh;

    void InitializeUpdates()
    {
        installedVersion = Updates.Installed(session.Root);
        if (!preview && installedVersion is not null) updater = Updates.Manager(installedVersion);
        dismissedUpdate = session.Config["dismissedUpdate"]?.GetValue<string>();
        async Task CheckIfDue()
        {
            if (session.Config["checkForUpdates"]?.GetValue<bool>() != false && DateTime.UtcNow - lastUpdateAttempt >= TimeSpan.FromDays(1))
                await CheckUpdates();
        }
        Activated += async (_, _) => await CheckIfDue();
        updateTimer.Tick += async (_, _) => await CheckIfDue();
        updateTimer.Start();
        Closed += (_, _) => { updateTimer.Stop(); updateLifetime.Cancel(); updateLifetime.Dispose(); };
    }

    async Task CheckUpdates()
    {
        if (checkingUpdates || updater is null || updateLifetime.IsCancellationRequested) return;
        checkingUpdates = true; lastUpdateAttempt = DateTime.UtcNow;
        updateStatus = "Checking for updates…"; RefreshUpdates();
        try
        {
            availableUpdate = await Updates.Check(updater, updateLifetime.Token);
            updateStatus = availableUpdate is null ? "You’re up to date." : "";
        }
        catch (Exception error) when (error is not OperationCanceledException || !updateLifetime.IsCancellationRequested)
        {
            updateStatus = error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests }
                ? "GitHub is limiting update checks from this network. QFT+ tries again later."
                : "Couldn’t check for updates. Check the internet connection, then try again.";
        }
        finally { checkingUpdates = false; if (!updateLifetime.IsCancellationRequested) RefreshUpdates(); }
    }

    void RefreshUpdates()
    {
        UpdateBanner.Visibility = page == "Tracking" && availableUpdate is not null && AvailableVersion != dismissedUpdate
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateMessage.Text = availableUpdate is null ? "" : installingUpdate ? updateStatus
            : $"QFT+ {AvailableVersion} is available. Installing takes a few minutes"
              + (session.Running ? " and stops tracking until it’s done" : "") + ". Your settings, calibrations, and recordings stay.";
        InstallUpdateButton.Visibility = availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        InstallUpdateButton.IsEnabled = !installingUpdate;
        UpdateProgress.Visibility = installingUpdate && updatePercent >= 0 ? Visibility.Visible : Visibility.Collapsed; UpdateProgress.Value = Math.Max(0, updatePercent);
        FeedbackBanner.Visibility = page == "Tracking" && DiscordProfile.Length > 0 && session.Config["feedbackDismissed"]?.GetValue<bool>() != true && Calibrated()
            ? Visibility.Visible : Visibility.Collapsed;
        updateRefresh?.Invoke();
    }

    bool Calibrated() => File.Exists(session.Config["faceEnrollment"]?.GetValue<string>()) || File.Exists(Path.Combine(session.Root, "calibration/qpro-pupil-dilation.json"));
    void ViewUpdate(object sender, RoutedEventArgs e) => OpenUpdate();
    async void InstallUpdateClick(object sender, RoutedEventArgs e) => await InstallUpdate();

    async Task InstallUpdate()
    {
        if (installingUpdate || updater is null || availableUpdate is not { } info) { if (availableUpdate is null) OpenUpdate(); return; }
        if (busy || recording || training) { Error("Finish calibration or setup, then install the update."); return; }
        installingUpdate = true; updatePercent = 0; updateStatus = "Downloading the update…"; RefreshUpdates();
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        var stall = TimeSpan.FromMinutes(2); stalled.CancelAfter(stall);
        IProgress<int> shown = new Progress<int>(percent => { updatePercent = percent; updateStatus = $"Downloading the update… {percent}%"; RefreshUpdates(); });
        try
        {
            await updater.DownloadUpdatesAsync(info, percent => { stalled.CancelAfter(stall); shown.Report(percent); }, stalled.Token);
            updateStatus = "Installing the update…"; RefreshUpdates();
            updater.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: false, restart: true);
            await Quit();
        }
        catch (Exception error) when (!updateLifetime.IsCancellationRequested)
        {
            installingUpdate = false; updatePercent = -1;
            updateStatus = stalled.IsCancellationRequested ? "The download stopped making progress. Check the internet connection, then try again."
                : "Couldn’t install the update. Check the internet connection, then try again. (" + error.Message + ")";
            Error(updateStatus);
        }
        finally { if (!updateLifetime.IsCancellationRequested) RefreshUpdates(); }
    }
    void UninstallOptions()
    {
        if (WorkingCopy.Installed() is null) return;
        var panel = new StackPanel();
        var heading = Text("Uninstall", 16); heading.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2); panel.Children.Add(heading);
        panel.Children.Add(Text("Removes QFT+, its SteamVR driver, and its VRCFaceTracking module. Your calibrations, recordings, and settings stay unless you choose to delete them too.", 13, true));
        var button = AsyncButton("Uninstall QFT+…", UninstallApp); button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new(0, 8, 0, 0);
        panel.Children.Add(button);
        Page.Children.Add(Card(panel));
    }

    async Task UninstallApp()
    {
        if (busy || recording || training) { Error("Finish calibration or setup, then uninstall."); return; }
        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var everything = new System.Windows.Forms.TaskDialogVerificationCheckBox("Also delete calibrations, recordings, face-tracking logs, and settings");
        var uninstall = new System.Windows.Forms.TaskDialogButton("Uninstall");
        if (System.Windows.Forms.TaskDialog.ShowDialog(owner, new System.Windows.Forms.TaskDialogPage
        {
            Caption = "Uninstall QFT+", Heading = "Uninstall QFT+?", Verification = everything,
            Text = "Removes the app, its SteamVR driver, its VRCFaceTracking module, and its shortcuts. Tracking stops.",
            Buttons = { uninstall, System.Windows.Forms.TaskDialogButton.Cancel }, DefaultButton = uninstall,
        }) != uninstall) return;
        while (Uninstall.Blocker() is { } blocker)
            if (System.Windows.Forms.TaskDialog.ShowDialog(owner, new System.Windows.Forms.TaskDialogPage
                { Caption = "Uninstall QFT+", Heading = "Close an app first", Text = blocker,
                  Buttons = { System.Windows.Forms.TaskDialogButton.Retry, System.Windows.Forms.TaskDialogButton.Cancel } }) != System.Windows.Forms.TaskDialogButton.Retry) return;
        if (everything.Checked) File.WriteAllText(Uninstall.EverythingMarker, "Settings > Uninstall: also delete the data");
        else File.Delete(Uninstall.EverythingMarker);
        var update = Velopack.Locators.VelopackLocator.Current.UpdateExePath;
        if (update is null || !File.Exists(update)) { Error("QFT+ couldn’t find its uninstaller. Uninstall it from Windows Settings > Apps."); return; }
        await session.Stop();
        using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(update, "--uninstall --silent") { UseShellExecute = false })) { }
        await Quit();
    }

    void OpenUpdate()
    {
        try { Open(Updates.Url(availableUpdate)); }
        catch (Exception) { Error("Couldn’t open your browser. Visit github.com/Yeusepe/QFTPlus/releases to get the update."); }
    }

    const string DiscordProfile = "https://discord.com/users/1070533060736602133";
    void MessageOnDiscord(object sender, RoutedEventArgs e)
    {
        try { Open(DiscordProfile); }
        catch (Exception) { Error("Couldn’t open your browser. Visit " + DiscordProfile + " to send a message."); }
    }

    void DismissFeedback(object sender, RoutedEventArgs e)
    {
        try { session.Save("feedbackDismissed", true); }
        catch (Exception) { Error("The request is hidden for now. Your preference couldn’t be saved."); }
        FeedbackBanner.Visibility = Visibility.Collapsed;
    }

    void DismissUpdate(object sender, RoutedEventArgs e)
    {
        dismissedUpdate = AvailableVersion;
        try { session.Save("dismissedUpdate", dismissedUpdate); }
        catch (Exception) { Error("The update is hidden for now. Your preference couldn’t be saved."); }
        RefreshUpdates();
    }

    void UpdateOptions()
    {
        var panel = new StackPanel();
        var heading = Text("Updates", 16); heading.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2); panel.Children.Add(heading);
        panel.Children.Add(Text(installedVersion is null ? "The installed version couldn’t be read." : $"Installed version: {installedVersion}", 14, true));
        var status = Text(""); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); panel.Children.Add(status);
        var downloading = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new(0, 0, 0, 12) }; downloading.SetResourceReference(ProgressBar.ForegroundProperty, "Accent");
        AutomationProperties.SetName(downloading, "Update download"); panel.Children.Add(downloading);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        var check = AsyncButton("Check for updates", CheckUpdates);
        var install = AsyncButton("Install update", InstallUpdate);
        var view = Button("View releases", OpenUpdate);
        foreach (var button in new[] { install, check, view }) { button.Margin = new(0, 0, 8, 8); actions.Children.Add(button); }
        var automatic = new CheckBox { Content = new TextBlock { Text = "Check for updates automatically" }, IsChecked = session.Config["checkForUpdates"]?.GetValue<bool>() != false };
        automatic.Click += (_, _) => session.Save("checkForUpdates", automatic.IsChecked == true);
        panel.Children.Add(automatic);
        panel.Children.Add(Text(updater is null ? "This copy wasn’t installed by QFT+ Setup, so it doesn’t update itself."
            : (installedVersion?.IsPrerelease == true ? "Includes release candidates. " : "") + "Updates install here and keep your settings, calibrations, and recordings.", 13, true));
        Page.Children.Add(Card(panel));
        updateRefresh = () =>
        {
            check.IsEnabled = !checkingUpdates && updater is not null;
            install.Visibility = availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            install.IsEnabled = !installingUpdate;
            check.Content = checkingUpdates ? "Checking…" : "Check for updates";
            view.Content = availableUpdate is null ? "View releases" : "View update";
            status.Text = updateStatus.Length > 0 ? updateStatus : availableUpdate is not null ? $"QFT+ {AvailableVersion} is available." : "";
            status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            downloading.Visibility = UpdateProgress.Visibility; downloading.Value = UpdateProgress.Value;
        };
        updateRefresh();
    }
}
