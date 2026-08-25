using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace RAM.Core.Roblox.Rdd;

/// <summary>
/// Downloads a full Roblox deployment for a given version GUID and lays it out into a
/// version-* folder, tagged with the selected exploit (or "Default"). Works directly
/// against Roblox's own deployment CDN — no third-party server required.
///
/// Installs are staged: everything is downloaded, verified and extracted into a hidden
/// ".staging-" folder under the install root, and only then atomically renamed into the
/// final version-* folder. A cancelled or failed install therefore never leaves a
/// half-written deployment, never touches the previous install, and a same-version
/// reinstall swaps the old folder out (rename-aside → rename-in → delete) instead of
/// deleting it first.
/// </summary>
public sealed class RobloxDeploymentService
{
    public const string TagFileName = ".ram-tag";

    /// <summary>How many manifest files are downloaded at once (the reference RDD page's default).</summary>
    private const int MaxParallelDownloads = 4;

    /// <summary>Serializes extraction across files so concurrent downloads can't write the same path.</summary>
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly HttpClient _http;

    public RobloxDeploymentService(HttpClient? http = null)
    {
        // A shared client is reused across service instances so repeated downloads
        // (and app navigations) don't churn sockets or lose connection reuse.
        _http = http ?? SharedHttp;
    }

    /// <summary>
    /// Resolve the current client version GUID (e.g. "version-d584fb6c717a43d9") from
    /// Roblox's channel API. This app only ever downloads the Windows player build.
    /// </summary>
    public async Task<string> ResolveCurrentVersionAsync(CancellationToken ct = default)
    {
        const string windowsPlayer = "WindowsPlayer";
        string url = $"https://clientsettings.roblox.com/v2/client-version/{windowsPlayer}?channel=LIVE";
        string json = await _http.GetStringAsync(url, ct);
        return Utilities.TryParseJson<Newtonsoft.Json.Linq.JObject>(json, out var data)
            ? data["clientVersionUpload"]?.ToString() ?? throw new InvalidOperationException("clientVersionUpload missing in response")
            : throw new InvalidOperationException("Invalid client-version response");
    }

    public async Task<List<RddManifestEntry>> FetchManifestAsync(string versionGuid, CancellationToken ct = default)
    {
        string url = string.Format(RddManifestParser.ManifestUrlFormat, versionGuid);
        string manifest = await _http.GetStringAsync(url, ct);
        return RddManifestParser.Parse(manifest);
    }

    /// <summary>
    /// Download and extract a deployment into <paramref name="targetRoot"/>/&lt;versionGuid&gt;.
    /// Manifest files are streamed to unique per-run temp files and downloaded in parallel
    /// (bounded, like the reference RDD page's "Parallel Downloads" checkbox; disable with
    /// <paramref name="parallelDownloads"/>) and extracted asynchronously, entry by entry — or
    /// copied raw when the entry isn't a zip (e.g. the installer exe). Cancellable mid-file,
    /// with no temp residue on normal completion. Everything lands in a hidden staging folder
    /// under <paramref name="targetRoot"/> and is atomically renamed into the final version
    /// folder only once the whole deployment is verified — see the class comment. Returns the
    /// version folder.
    /// </summary>
    public async Task<string> InstallAsync(
        string versionGuid,
        string targetRoot,
        string? tag = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        bool parallelDownloads = true)
    {
        var versionFolder = Path.Combine(targetRoot, versionGuid);
        Directory.CreateDirectory(targetRoot);

        var entries = await FetchManifestAsync(versionGuid, ct);
        if (entries.Count == 0)
            throw new InvalidOperationException($"No deployment files found for {versionGuid}");

        var tmpDir = Path.Combine(Path.GetTempPath(), $"ram-rdd-{versionGuid}-{Guid.NewGuid():N}");
        // Same volume as the final folder (it lives under targetRoot), so the final rename
        // is atomic. The dot prefix keeps it invisible to RddDeploymentStore.ListInstalls.
        var staging = Path.Combine(targetRoot, $".staging-{versionGuid}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        Directory.CreateDirectory(staging);

        try
        {
            long total = entries.Sum(e => e.CompressedSize);
            long completed = 0;
            long Completed() => Interlocked.Read(ref completed);

            int maxParallel = parallelDownloads ? MaxParallelDownloads : 1;
            using var gate = new SemaphoreSlim(maxParallel, maxParallel);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            async Task ProcessEntryAsync(RddManifestEntry entry)
            {
                await gate.WaitAsync(linkedCts.Token);
                try
                {
                    linkedCts.Token.ThrowIfCancellationRequested();

                    string url = string.Format(RddManifestParser.FileUrlFormat, versionGuid, entry.FileName);
                    string tmpZip = Path.Combine(tmpDir, entry.FileName);

                    progress?.Report(new InstallProgress(InstallPhase.Downloading, entry.FileName, Completed(), total,
                        $"Downloading {entry.FileName} ({entry.CompressedSize / 1024.0 / 1024.0:0.0} MB)"));

                    using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token))
                    {
                        resp.EnsureSuccessStatusCode();
                        await using var fs = File.Create(tmpZip);
                        await CopyWithProgressAsync(
                            await resp.Content.ReadAsStreamAsync(linkedCts.Token), fs,
                            entry.FileName, Completed, total, progress, linkedCts.Token);
                    }

                    // Integrity check: the manifest carries each file's MD5 (of the downloaded
                    // archive). A corrupted-but-complete download must fail here, not install
                    // silently into a broken Roblox tree. Scoped block: the verify stream must be
                    // closed before tmpZip is deleted below.
                    {
                        await using var verifyStream = File.OpenRead(tmpZip);
                        string actual = Convert.ToHexString(await MD5.HashDataAsync(verifyStream, linkedCts.Token))
                            .ToLowerInvariant();
                        if (!string.Equals(actual, entry.Md5, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException(
                                $"Checksum mismatch for {entry.FileName} (expected {entry.Md5}, got {actual}).");
                    }

                    // The download slice is done — count it into the cumulative progress.
                    Interlocked.Add(ref completed, entry.CompressedSize);

                    // Extraction is serialized across files (single gate), so two zips can never
                    // write the same path concurrently and per-file extraction progress can't
                    // interleave. Downloads stay parallel behind it.
                    await ExtractionGate.WaitAsync(linkedCts.Token);
                    try
                    {
                        // Manifest entries are mostly zips, but some are raw files (e.g. the
                        // RobloxPlayerInstaller.exe shipped with every deployment). Extract zips;
                        // copy everything else into the install as-is.
                        if (IsZipArchive(tmpZip))
                        {
                            progress?.Report(new InstallProgress(InstallPhase.Extracting, entry.FileName, 0, entry.UncompressedSize,
                                $"Extracting {entry.FileName}"));
                            await ExtractZipAsync(tmpZip, staging, entry, progress, linkedCts.Token);
                        }
                        else
                        {
                            CopyRawEntry(tmpZip, staging, entry.FileName);
                        }
                    }
                    finally
                    {
                        ExtractionGate.Release();
                    }

                    File.Delete(tmpZip);
                }
                finally
                {
                    gate.Release();
                }
            }

            var tasks = entries.Select(entry => ProcessEntryAsync(entry)).ToArray();
            // Abort the remaining downloads as soon as any single file fails, instead of
            // letting the rest run to completion (or hang up to the HTTP timeout) for nothing.
            foreach (var task in tasks)
                _ = task.ContinueWith(t =>
                {
                    if (t.IsFaulted || t.IsCanceled)
                    {
                        try { linkedCts.Cancel(); } catch (ObjectDisposedException) { /* app closing */ }
                    }
                }, TaskScheduler.Default);

            await Task.WhenAll(tasks);

            // Tag before the swap so no visible folder is ever untagged.
            if (!string.IsNullOrEmpty(tag))
                File.WriteAllText(Path.Combine(staging, TagFileName), tag);

            SwapIntoPlace(staging, versionFolder);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
        }

        progress?.Report(new InstallProgress(InstallPhase.Done, "", 0, 0, "Done"));
        return versionFolder;
    }

    /// <summary>
    /// Atomically move a fully staged deployment into its final folder. A fresh version
    /// folder is a plain rename. An existing same-version folder (force re-download) is
    /// moved aside first, the staged copy renamed in, then the old one deleted — so the
    /// previous install survives until the new one is fully in place, and a failed swap
    /// rolls the old folder back instead of leaving nothing.
    /// </summary>
    private static void SwapIntoPlace(string staging, string versionFolder)
    {
        if (!Directory.Exists(versionFolder))
        {
            Directory.Move(staging, versionFolder);
            return;
        }

        string stale = Path.Combine(
            Path.GetDirectoryName(versionFolder)!,
            $".stale-{new DirectoryInfo(versionFolder).Name}-{Guid.NewGuid():N}");
        Directory.Move(versionFolder, stale);
        try
        {
            Directory.Move(staging, versionFolder);
        }
        catch
        {
            // Restore the previous install — a failed swap must never leave nothing.
            Directory.Move(stale, versionFolder);
            throw;
        }
        try { Directory.Delete(stale, true); } catch { /* best effort — invisible dot-folder */ }
    }

    /// <summary>
    /// Stream one downloaded file to disk, reporting cumulative bytes against the whole
    /// deployment. The completed base is re-read on every chunk (files finish concurrently),
    /// and the total is clamped so a brief read can never over-report.
    /// </summary>
    private static async Task CopyWithProgressAsync(
        Stream source, Stream destination,
        string fileName, Func<long> completedBase, long total,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        byte[] buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await source.ReadAsync(buffer, ct)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            long done = Math.Min(total, completedBase() + read);
            progress?.Report(new InstallProgress(InstallPhase.Downloading, fileName, done, total, ""));
        }
    }

    /// <summary>True when the file is a readable zip archive. Anything else (raw executable,
    /// empty file, …) is a non-zip manifest entry and is copied into the install instead.</summary>
    private static bool IsZipArchive(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Copy a raw (non-zip) manifest entry into the version folder, applying the same
    /// path-traversal guard as zip extraction.</summary>
    private static void CopyRawEntry(string source, string versionFolder, string fileName)
    {
        string target = Path.GetFullPath(Path.Combine(versionFolder, fileName));
        string versionRoot = Path.GetFullPath(versionFolder);
        if (!target.StartsWith(versionRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Manifest entry escapes the install folder: {fileName}");

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
    }

    private static async Task ExtractZipAsync(
        string zipPath, string versionFolder, RddManifestEntry entry,
        IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        string versionRoot = Path.GetFullPath(versionFolder);
        using var zip = ZipFile.OpenRead(zipPath); // seekable temp file — safe with any zip

        long extracted = 0;
        foreach (var zipEntry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(zipEntry.Name)) continue; // directory entries

            string target = Path.GetFullPath(Path.Combine(versionRoot, zipEntry.FullName));

            // Zip-slip guard: extracted entries must stay inside the version folder.
            if (!target.StartsWith(versionRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"Zip entry escapes the install folder: {zipEntry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var input = zipEntry.Open();
            await using var output = File.Create(target);
            await input.CopyToAsync(output, ct);
            extracted += zipEntry.Length;
            progress?.Report(new InstallProgress(InstallPhase.Extracting, entry.FileName, extracted, entry.UncompressedSize, ""));
        }
    }
}
