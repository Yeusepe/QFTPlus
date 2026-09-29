using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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

internal sealed record AppRelease(ReleaseVersion Version, string Tag)
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

    internal static async Task<AppRelease?> Check(HttpClient client, ReleaseVersion current, CancellationToken cancel)
    {
        AppRelease? newest = null;
        for (var page = 1; ; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/Yeusepe/QFTPlus/releases?per_page=100&page={page}");
            request.Headers.UserAgent.ParseAdd("QFTPlus/" + current.Text);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await client.SendAsync(request, cancel);
            response.EnsureSuccessStatusCode();
            var releases = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel))?.AsArray()
                ?? throw new JsonException("Missing release list.");
            foreach (var release in releases)
            {
                if (release is not JsonObject item || item["draft"]?.GetValue<bool>() != false) continue;
                var tag = item["tag_name"]?.GetValue<string>();
                var version = ReleaseVersion.Parse(tag);
                if (version is null || version.CompareTo(current) <= 0 || newest is not null && version.CompareTo(newest.Version) <= 0) continue;
                if (current.Preview.Length == 0 && (item["prerelease"]?.GetValue<bool>() != false || version.Preview.Length > 0)) continue;
                if (item["assets"] is not JsonArray assets || !assets.Any(a =>
                    a?["name"]?.GetValue<string>() == $"QFT-Plus-{version.Text}-Setup.exe" && a?["state"]?.GetValue<string>() == "uploaded")) continue;
                newest = new(version, tag!);
            }
            if (releases.Count < 100) return newest;
        }
    }
}
