using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace QFTPlus;

internal sealed record ReleaseVersion(string Text, Version Core, string[] Preview) : IComparable<ReleaseVersion>
{
    internal static ReleaseVersion? Parse(string? text)
    {
        if (text is null || text.Length > 128) return null;
        var match = Regex.Match(text, @"^v?((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var core)) return null;
        var preview = match.Groups[2].Success ? match.Groups[2].Value.Split('.') : Array.Empty<string>();
        if (preview.Any(p => Numeric(p) && p.Length > 1 && p[0] == '0')) return null;
        return new(text.TrimStart('v'), core, preview);
    }

    static bool Numeric(string text) => text.All(c => c is >= '0' and <= '9');
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        var order = Core.CompareTo(other.Core);
        if (order != 0) return order;
        if (Preview.Length == 0 || other.Preview.Length == 0)
            return (Preview.Length == 0).CompareTo(other.Preview.Length == 0);
        for (var i = 0; i < Math.Min(Preview.Length, other.Preview.Length); i++)
        {
            var a = Preview[i]; var b = other.Preview[i];
            var an = Numeric(a); var bn = Numeric(b);
            order = an != bn ? (an ? -1 : 1) : an && a.Length != b.Length
                ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            if (order != 0) return order;
        }
        return Preview.Length.CompareTo(other.Preview.Length);
    }
}

internal sealed record AppRelease(ReleaseVersion Version, string Tag, UpdateInfo? Info = null)
{
    internal string Url => Updates.ReleasesUrl + "/tag/" + Uri.EscapeDataString(Tag);
}

internal static class Updates
{
    internal const string ReleasesUrl = "https://github.com/Yeusepe/QFTPlus/releases";
    internal static ReleaseVersion? Installed(string root)
    {
        try { return ReleaseVersion.Parse(JsonNode.Parse(File.ReadAllText(Path.Combine(root, "release-manifest.json")))?["version"]?.GetValue<string>()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    internal static UpdateManager? Manager(ReleaseVersion current)
    {
        var manager = new UpdateManager(new GithubSource("https://github.com/Yeusepe/QFTPlus", null, current.Preview.Length > 0));
        return manager.IsInstalled ? manager : null;
    }

    internal static async Task<AppRelease?> Check(UpdateManager manager, CancellationToken cancel)
    {
        var info = await manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(30), cancel);
        return info is not null && ReleaseVersion.Parse(info.TargetFullRelease.Version.ToString()) is { } version ? new(version, "v" + version.Text, info) : null;
    }
}
