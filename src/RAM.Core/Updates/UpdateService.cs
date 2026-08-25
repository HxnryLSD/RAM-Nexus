using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace RAM.Core.Updates;

/// <summary>A newer app release found by <see cref="UpdateService"/>.</summary>
public sealed record UpdateInfo(string TagName, string Version, string DownloadUrl);

/// <summary>Byte-level progress for an update download.</summary>
public sealed record UpdateProgress(long BytesDone, long BytesTotal);

/// <summary>
/// Checks for, downloads, and applies app updates, reusing the same streaming download
/// pattern as the RDD deployment pipeline. The version manifest is a small JSON document
/// (by default the GitHub releases API) with a "tag_name" and at least one asset whose
/// "name" contains "update" and ends in ".zip" (falling back to the first asset).
/// Updates are installed by <see cref="StageUpdateAsync"/> (extract to a sibling
/// <c>app-&lt;version&gt;</c> folder, never touching the live one) followed by
/// <see cref="ApplyAndRestart"/> (detached helper swaps the staged folder in after this
/// process exits — rename-based, so files removed in the new build actually go away).
/// </summary>
public sealed class UpdateService
{
    /// <summary>Fallback release source when no URL was baked in at build time.</summary>
    public const string UpstreamManifestUrl = "https://api.github.com/repos/ic3w0lf22/Roblox-Account-Manager/releases/latest";

    /// <summary>
    /// Default release source for this build. The release workflow (.github/workflows/release.yml)
    /// bakes the fork's own releases API URL in via the <c>UpdateManifestUrl</c> MSBuild
    /// property, so a CI-built app always checks the repo it was released from; local builds
    /// fall back to the upstream repo (which publishes no built releases, so the check
    /// harmlessly reports "no update"). Override at runtime with the UpdateManifestUrl setting.
    /// </summary>
    public static string DefaultManifestUrl { get; } =
        typeof(UpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateManifestUrl")?.Value
        ?? UpstreamManifestUrl;

    /// <summary>Name of the root-level executable every update package must contain.</summary>
    public const string AppExeName = "Roblox Account Manager.exe";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _http;
    public string ManifestUrl { get; }

    public UpdateService(string? manifestUrl = null, HttpClient? http = null)
    {
        ManifestUrl = (manifestUrl ?? DefaultManifestUrl).TrimEnd('/');
        _http = http ?? SharedHttp;
        UserAgent.Apply(_http);
    }

    /// <summary>
    /// Fetch the latest release; null when the manifest is unreachable, unparseable, or not
    /// newer than <paramref name="currentVersion"/>.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(Version currentVersion, CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(ManifestUrl, ct);
            var release = JObject.Parse(json);

            string tag = release.Value<string>("tag_name") ?? "";
            // Prefer the in-app update zip (our releases carry Setup.exe + portable.zip +
            // update.zip); fall back to the first asset for third-party manifests.
            string? asset = release["assets"]
                ?.Where(a => a.Value<string>("name") is string n
                    && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    && n.Contains("update", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault()?.Value<string>("browser_download_url")
                ?? release["assets"]?.FirstOrDefault()?.Value<string>("browser_download_url");
            if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(asset)) return null;

            var version = ParseVersion(tag);
            if (version is null || version <= currentVersion) return null;

            return new UpdateInfo(tag, version.ToString(3), asset);
        }
        catch (OperationCanceledException)
        {
            // Cancellation must stay cancellation, not be conflated with "no update".
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Download the update asset into <paramref name="destinationDir"/> (created if needed)
    /// and return the finished zip path. Progress is reported in bytes.
    /// </summary>
    public async Task<string> DownloadAsync(
        UpdateInfo info, string destinationDir,
        IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(destinationDir);
        string tmp = Path.Combine(destinationDir, $"update-{info.Version}.part");
        string final = Path.Combine(destinationDir, $"update-{info.Version}.zip");

        try
        {
            long total = 0;
            long read = 0;
            using (var resp = await _http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                total = resp.Content.Headers.ContentLength ?? 0;

                await using var fs = File.Create(tmp);
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);

                byte[] buffer = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    progress?.Report(new UpdateProgress(read, total));
                }
            } // fs/stream disposed here — the file is no longer locked

            // A truncated download would be extracted into a broken app folder on restart.
            if (total > 0 && read != total)
                throw new IOException($"Incomplete download: received {read} of {total} bytes.");

            File.Move(tmp, final, overwrite: true);
            return final;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Extract <paramref name="updateZip"/> into a sibling <c>app-&lt;version&gt;</c> folder of
    /// the app directory — same volume as the final rename, and the live app folder is never
    /// touched, so a corrupt package or a crash mid-extract can't damage the running install.
    /// The zip must contain <see cref="AppExeName"/> at its root (the release pipeline builds
    /// exactly this layout); anything else is rejected before a single file is written.
    /// </summary>
    public static async Task<string> StageUpdateAsync(
        string updateZip, string version, string? appDir = null, CancellationToken ct = default)
    {
        appDir ??= AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string parent = Path.GetDirectoryName(appDir)
            ?? throw new InvalidOperationException($"Cannot stage an update next to '{appDir}'.");
        string staged = Path.Combine(parent, $"app-{version}");

        ValidateUpdateZip(updateZip);

        // A stale folder from an earlier aborted staging attempt must not mix with the new
        // extraction (files deleted in the new build would otherwise linger).
        if (Directory.Exists(staged)) Directory.Delete(staged, true);
        Directory.CreateDirectory(staged);
        // ZipFile.ExtractToDirectoryAsync is .NET 9+; the sync overload is fine off the UI
        // thread and the caller is a page handler awaiting this.
        await Task.Run(() => ZipFile.ExtractToDirectory(updateZip, staged, overwriteFiles: true), ct);
        return staged;
    }

    private static void ValidateUpdateZip(string updateZip)
    {
        using var zip = ZipFile.OpenRead(updateZip);
        if (!zip.Entries.Any(e => string.Equals(e.Name, AppExeName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Not a valid update package: missing '{AppExeName}'.");
    }

    /// <summary>
    /// Detach a helper that waits for this process to exit, then swaps <paramref name="stagedDir"/>
    /// (produced by <see cref="StageUpdateAsync"/>) into the app folder with two renames — the
    /// old folder is moved aside, the staged one renamed in, the old one deleted. Rename-based
    /// replacement means files removed in the new build actually go away (Expand-Archive -Force
    /// can't). If the swap can't run (locked folder, missing staged dir), it falls back to
    /// extracting <paramref name="updateZip"/> over the app folder — the previous approach.
    /// The helper then starts the new build. Callers must exit the process right after.
    /// </summary>
    public static void ApplyAndRestart(string updateZip, string stagedDir)
    {
        string appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string exe = Environment.ProcessPath ?? Path.Combine(appDir, AppExeName);

        // PowerShell runs detached: waits for this process to exit, swaps the staged folder in
        // (rename aside → rename in → delete old; rollback restores the old folder if the
        // second rename fails), falls back to Expand-Archive over the app folder, then starts
        // the new build. Paths in single quotes; doubled inside PS.
        string q(string s) => s.Replace("'", "''");
        string ps = string.Join("; ",
            "$ErrorActionPreference = 'Stop'",
            $"Wait-Process -Id {Environment.ProcessId}",
            "Start-Sleep -Milliseconds 800",
            $"$live = '{q(appDir)}'",
            $"$staged = '{q(stagedDir)}'",
            $"$exe = '{q(exe)}'",
            $"$zip = '{q(updateZip)}'",
            "$old = $live + '.old-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)",
            "$swapped = $false",
            "try {",
            "  Rename-Item -LiteralPath $live -NewName (Split-Path $old -Leaf)",
            "  try {",
            "    Rename-Item -LiteralPath $staged -NewName (Split-Path $live -Leaf)",
            "    $swapped = $true",
            "  } catch {",
            "    Rename-Item -LiteralPath $old -NewName (Split-Path $live -Leaf)",
            "    throw",
            "  }",
            "} catch {",
            "  Expand-Archive -LiteralPath $zip -DestinationPath $live -Force",
            "}",
            "if ($swapped) { Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue }",
            $"Start-Process -FilePath $exe");

        var psi = new ProcessStartInfo("cmd.exe", $"/c start \"\" /min powershell -NoProfile -WindowStyle Hidden -Command \"{ps}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);
    }

    /// <summary>
    /// Delete leftover <c>.old-*</c> folders from an interrupted swap (a crash between the
    /// rename-aside and rename-in steps). Called at startup. Staged <c>app-*</c> folders are
    /// intentionally left alone — they may be a pending update.
    /// </summary>
    public static void SweepStaleUpdateArtifacts(string? appDir = null)
    {
        appDir ??= AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(appDir);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return;

        foreach (string dir in Directory.GetDirectories(parent, ".old-*", SearchOption.TopDirectoryOnly))
        {
            try { Directory.Delete(dir, true); } catch { /* locked — leave for next boot */ }
        }
    }

    /// <summary>Parse a release tag like "v1.2.3" or "1.2.3-beta" into a version, or null.</summary>
    private static Version? ParseVersion(string tag)
    {
        string s = tag.TrimStart('v', 'V');
        int cut = s.IndexOf('-');
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var version) ? version : null;
    }
}
