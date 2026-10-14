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
    internal Velopack.UpdateInfo? availableUpdate;
    string? AvailableVersion => availableUpdate?.TargetFullRelease.Version.ToString();
    internal string? dismissedUpdate;
    internal string updateStatus = "";
    internal bool checkingUpdates;
    int updatePercent = -1;
    internal DateTime lastUpdateAttempt = DateTime.MinValue;
    Action? updateRefresh;

    void InitializeUpdates()
    {
        installedVersion = Updates.Installed();
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
        updateStatus = Loc.Get("CheckingForUpdates"); RefreshUpdates();
        try
        {
            availableUpdate = await Updates.Check(updater, updateLifetime.Token);
            updateStatus = availableUpdate is null ? Loc.Get("YouReUpToDate") : "";
        }
        catch (Exception error) when (error is not OperationCanceledException || !updateLifetime.IsCancellationRequested)
        {
            updateStatus = error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests }
                ? Loc.Get("GitHubIsLimitingUpdateChecksFromThisNetworkQFT")
                : Loc.Get("CouldnTCheckForUpdatesCheckTheInternetConnection");
        }
        finally { checkingUpdates = false; if (!updateLifetime.IsCancellationRequested) RefreshUpdates(); }
    }

    internal void RefreshUpdates()
    {
        UpdateBanner.Visibility = page == "Tracking" && availableUpdate is not null && AvailableVersion != dismissedUpdate
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateMessage.Message = availableUpdate is null ? "" : installingUpdate ? updateStatus
            : Loc.Format(session.Running ? "UpdateAvailableStopsTracking" : "UpdateAvailableDetails", AvailableVersion);
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
        if (busy || recording || training) { Error(Loc.Get("FinishCalibrationOrSetupThenInstallTheUpdate")); return; }
        installingUpdate = true; updatePercent = 0; updateStatus = Loc.Get("DownloadingTheUpdate_a14a1b55"); RefreshUpdates();
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        var stall = TimeSpan.FromMinutes(2); stalled.CancelAfter(stall);
        IProgress<int> shown = new Progress<int>(percent => { updatePercent = percent; updateStatus = Loc.Format("DownloadingTheUpdate", percent); RefreshUpdates(); });
        try
        {
            await updater.DownloadUpdatesAsync(info, percent => { stalled.CancelAfter(stall); shown.Report(percent); }, stalled.Token);
            updateStatus = Loc.Get("InstallingTheUpdate"); RefreshUpdates();
            updater.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: false, restart: true);
            await Quit();
        }
        catch (Exception error) when (!updateLifetime.IsCancellationRequested)
        {
            installingUpdate = false; updatePercent = -1;
            updateStatus = stalled.IsCancellationRequested ? Loc.Get("TheDownloadStoppedMakingProgressCheckTheInternetConnection")
                : Loc.Format("UpdateInstallFailure", error.Message);
            Error(updateStatus);
        }
        finally { if (!updateLifetime.IsCancellationRequested) RefreshUpdates(); }
    }
    void UninstallOptions()
    {
        if (WorkingCopy.Installed() is null) return;
        var panel = new StackPanel();
        var heading = Text(Loc.Get("Uninstall"), 16); heading.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2); panel.Children.Add(heading);
        panel.Children.Add(Text(Loc.Get("RemovesQFTItsSteamVRDriverAndItsVRCFaceTrackingModule"), 13, true));
        var button = AsyncButton(Loc.Get("UninstallQFT_71877bb4"), UninstallApp); button.HorizontalAlignment = HorizontalAlignment.Left; button.Margin = new(0, 8, 0, 0);
        panel.Children.Add(button);
        Page.Children.Add(Card(panel));
    }

    async Task UninstallApp()
    {
        if (busy || recording || training) { Error(Loc.Get("FinishCalibrationOrSetupThenUninstall")); return; }
        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var everything = new System.Windows.Forms.TaskDialogVerificationCheckBox(Loc.Get("AlsoDeleteCalibrationsRecordingsFaceTrackingLogsAndSettings"));
        var uninstall = new System.Windows.Forms.TaskDialogButton(Loc.Get("Uninstall"));
        if (System.Windows.Forms.TaskDialog.ShowDialog(owner, new System.Windows.Forms.TaskDialogPage
        {
            Caption = Loc.Get("UninstallQFT"), Heading = Loc.Get("UninstallQFT_1021541b"), Verification = everything,
            Text = Loc.Get("RemovesTheAppItsSteamVRDriverItsVRCFaceTrackingModule"),
            Buttons = { uninstall, System.Windows.Forms.TaskDialogButton.Cancel }, DefaultButton = uninstall,
        }) != uninstall) return;
        while (Uninstall.Blocker() is { } blocker)
            if (System.Windows.Forms.TaskDialog.ShowDialog(owner, new System.Windows.Forms.TaskDialogPage
                { Caption = Loc.Get("UninstallQFT"), Heading = Loc.Get("CloseAnAppFirst"), Text = blocker,
                  Buttons = { System.Windows.Forms.TaskDialogButton.Retry, System.Windows.Forms.TaskDialogButton.Cancel } }) != System.Windows.Forms.TaskDialogButton.Retry) return;
        if (HeadsetModelExperiment.Enabled(session) || HeadsetModelExperiment.Pending(session))
        {
            busy = true;
            try { await session.SetHeadsetModel(false); }
            finally { busy = false; SidebarStatus(); trackingRefresh?.Invoke(); }
        }
        var update = Velopack.Locators.VelopackLocator.Current.UpdateExePath;
        if (update is null || !File.Exists(update)) { Error(Loc.Get("QFTCouldnTFindItsUninstallerUninstallItFrom")); return; }
        await session.Stop();
        if (everything.Checked) File.WriteAllText(Uninstall.EverythingMarker, "Settings > Uninstall: also delete the data");
        else File.Delete(Uninstall.EverythingMarker);
        try { using (System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(update, "--uninstall --silent") { UseShellExecute = false })) { } }
        catch { File.Delete(Uninstall.EverythingMarker); throw; }
        await Quit();
    }

    void OpenUpdate()
    {
        try { Open(Updates.Url(availableUpdate)); }
        catch (Exception) { Error(Loc.Get("CouldnTOpenYourBrowserVisitGithubComYeusepe")); }
    }

    const string DiscordProfile = "https://discord.com/users/1070533060736602133";
    void MessageOnDiscord(object sender, RoutedEventArgs e)
    {
        try { Open(DiscordProfile); }
        catch (Exception) { Error(Loc.Format("BrowserMessageFailure", DiscordProfile)); }
    }

    void DismissFeedback(object sender, RoutedEventArgs e)
    {
        try { session.Save("feedbackDismissed", true); }
        catch (Exception) { Error(Loc.Get("TheRequestIsHiddenForNowYourPreferenceCouldn")); }
        FeedbackBanner.Visibility = Visibility.Collapsed;
    }

    void DismissUpdate(object sender, RoutedEventArgs e)
    {
        dismissedUpdate = AvailableVersion;
        try { session.Save("dismissedUpdate", dismissedUpdate); }
        catch (Exception) { Error(Loc.Get("TheUpdateIsHiddenForNowYourPreferenceCouldn")); }
        RefreshUpdates();
    }

    void UpdateOptions()
    {
        var panel = new StackPanel();
        var heading = Text(Loc.Get("Updates"), 16); heading.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2); panel.Children.Add(heading);
        panel.Children.Add(Text(installedVersion is null ? Loc.Get("TheInstalledVersionCouldnTBeRead") : Loc.Format("InstalledVersion", installedVersion), 14, true));
        var status = Text(""); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); panel.Children.Add(status);
        var downloading = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new(0, 0, 0, 12) }; downloading.SetResourceReference(ProgressBar.ForegroundProperty, "Accent");
        AutomationProperties.SetName(downloading, Loc.Get("UpdateDownload")); panel.Children.Add(downloading);
        var actions = new WrapPanel(); panel.Children.Add(actions);
        var check = AsyncButton(Loc.Get("CheckForUpdates"), CheckUpdates);
        var install = AsyncButton(Loc.Get("InstallUpdate"), InstallUpdate);
        var view = Button(Loc.Get("ViewReleases"), OpenUpdate);
        foreach (var button in new[] { install, check, view }) { button.Margin = new(0, 0, 8, 8); actions.Children.Add(button); }
        var automatic = new CheckBox { Content = Loc.Get("CheckForUpdatesAutomatically"), IsChecked = session.Config["checkForUpdates"]?.GetValue<bool>() != false };
        automatic.Click += (_, _) => session.Save("checkForUpdates", automatic.IsChecked == true);
        System.Windows.Automation.AutomationProperties.SetHelpText(automatic, updater is null ? Loc.Get("ThisCopyWasnTInstalledByQFTSetupSo")
            : Loc.Get(installedVersion?.IsPrerelease == true ? "AutomaticPrereleaseUpdates" : "UpdatesInstallHereAndKeepYourSettingsCalibrationsAnd"));
        panel.Children.Add(automatic);
        Page.Children.Add(Card(panel));
        updateRefresh = () =>
        {
            check.IsEnabled = !checkingUpdates && updater is not null;
            install.Visibility = availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            install.IsEnabled = !installingUpdate;
            check.Content = checkingUpdates ? Loc.Get("Checking") : Loc.Get("CheckForUpdates");
            view.Content = availableUpdate is null ? Loc.Get("ViewReleases") : Loc.Get("ViewUpdate");
            status.Text = updateStatus.Length > 0 ? updateStatus : availableUpdate is not null ? Loc.Format("QFTIsAvailable", AvailableVersion) : "";
            status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            downloading.Visibility = UpdateProgress.Visibility; downloading.Value = UpdateProgress.Value;
        };
        updateRefresh();
    }
}
