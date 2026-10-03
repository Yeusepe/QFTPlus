using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace QFTPlus;

internal static class Updates
{
    internal const string ReleasesUrl = "https://github.com/Yeusepe/QFTPlus/releases";

    internal static string Url(UpdateInfo? update) =>
        update is null ? ReleasesUrl : ReleasesUrl + "/tag/v" + Uri.EscapeDataString(update.TargetFullRelease.Version.ToString());

    internal static SemanticVersion? Installed() => VelopackLocator.IsCurrentSet ? VelopackLocator.Current.CurrentlyInstalledVersion : null;

    internal static UpdateManager? Manager(SemanticVersion current)
    {
        var manager = new UpdateManager(new GithubSource("https://github.com/Yeusepe/QFTPlus", null, current.IsPrerelease));
        return manager.IsInstalled ? manager : null;
    }

    internal static Task<UpdateInfo?> Check(UpdateManager manager, CancellationToken cancel) =>
        manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
}
