using System.Text.Json;
using System.Text.Json.Nodes;
using Velopack;
using Velopack.Sources;

namespace QFTPlus;

internal static class Updates
{
    internal const string ReleasesUrl = "https://github.com/Yeusepe/QFTPlus/releases";

    internal static string Url(UpdateInfo? update) =>
        update is null ? ReleasesUrl : ReleasesUrl + "/tag/v" + Uri.EscapeDataString(update.TargetFullRelease.Version.ToString());

    internal static SemanticVersion? Installed(string root)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(root, "release-manifest.json")))?["version"]?.GetValue<string>() is { } text
                && SemanticVersion.TryParse(text, out var version) ? version : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    internal static UpdateManager? Manager(SemanticVersion current)
    {
        var manager = new UpdateManager(new GithubSource("https://github.com/Yeusepe/QFTPlus", null, current.IsPrerelease));
        return manager.IsInstalled ? manager : null;
    }

    internal static Task<UpdateInfo?> Check(UpdateManager manager, CancellationToken cancel) =>
        manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
}
