using RAM.Core.Infrastructure;

namespace RAM.Core.Roblox.Rdd;

/// <summary>
/// One file entry from a Roblox deployment manifest (rbxPkgManifest.txt).
/// Entries arrive in groups of four lines: filename, md5, uncompressed_size, compressed_size.
/// </summary>
public sealed record RddManifestEntry(string FileName, string Md5, long UncompressedSize, long CompressedSize);

public static class RddManifestParser
{
    public const string ManifestUrlFormat = "https://setup.rbxcdn.com/{0}-rbxPkgManifest.txt";
    public const string FileUrlFormat = "https://setup.rbxcdn.com/{0}-{1}";

    /// <summary>Parse manifest text into ordered file entries.</summary>
    public static List<RddManifestEntry> Parse(string manifest)
    {
        var entries = new List<RddManifestEntry>();
        using var reader = new StringReader(manifest);

        // First line is format version ("v0"); skip if present.
        string? line = reader.ReadLine();
        if (line is not null && line.Trim().StartsWith("v", StringComparison.OrdinalIgnoreCase))
            line = ReadNextNonEmpty(reader);

        while (line is not null)
        {
            string file = line.Trim();
            string? hash = ReadNextNonEmpty(reader);
            string? uncompressed = ReadNextNonEmpty(reader);
            string? compressed = ReadNextNonEmpty(reader);

            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(hash))
                break;

            entries.Add(new RddManifestEntry(
                file,
                hash,
                long.TryParse(uncompressed, out var u) ? u : 0,
                long.TryParse(compressed, out var c) ? c : 0));

            line = ReadNextNonEmpty(reader);
        }

        return entries;
    }

    private static string? ReadNextNonEmpty(StringReader reader)
    {
        string? l;
        while ((l = reader.ReadLine()) is not null)
            if (!string.IsNullOrWhiteSpace(l))
                return l;
        return null;
    }
}
