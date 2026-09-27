using System.IO.Compression;
using System.Security.Cryptography;

namespace QproFaceTracking.Setup;

internal static class PackageVerifier
{
    internal static void Verify(ZipArchive archive)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            if (parts.Length < 1 || parts.Any(p => p.Length == 0 || p is "." or ".." ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.EndsWith('.') || p.EndsWith(' ') ||
                IsDeviceName(p)) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                !entries.TryAdd(name, entry))
                throw new InvalidDataException("The package contains an unsafe or duplicate path.");
        }
        var sumsNames = entries.Keys.Where(n => n.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal)).ToArray();
        if (sumsNames.Length != 1) throw new InvalidDataException("The package file list is missing.");
        var sumsName = sumsNames[0];
        var prefix = sumsName[..^"SHA256SUMS.txt".Length];
        if (prefix.Count(c => c == '/') != 1 || entries.Keys.Any(n => !n.StartsWith(prefix, StringComparison.Ordinal)))
            throw new InvalidDataException("The package must contain one app folder.");
        var remaining = entries.Where(p => !p.Key.EndsWith('/') && p.Key != sumsName)
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(entries[sumsName].Open());
        foreach (var line in reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split("  ", 2, StringSplitOptions.None);
            if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit) ||
                !remaining.Remove(prefix + parts[1], out var entry))
                throw new InvalidDataException("The package file list is damaged.");
            using var content = entry.Open();
            if (!Convert.ToHexString(SHA256.HashData(content)).Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Damaged package file: " + parts[1]);
        }
        if (remaining.Count != 0) throw new InvalidDataException("The package contains unverified files.");
    }

    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9');
    }
}
