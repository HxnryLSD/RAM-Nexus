using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using RAM.Core.Infrastructure;
using RAM.Core.Roblox;
using RAM.Core.Roblox.FastFlags;
using RAM.Core.Roblox.Rdd;

namespace RAM.Core.Tests;

public class RddManifestParserTests
{
    // Fixture mirroring the real rbxPkgManifest.txt format we fetched.
    private const string RealManifest =
        "v0\r\n" +
        "RobloxApp.zip\r\naeaa4432a6005a7cead977db05447511\r\n134901428\r\n173551147\r\n" +
        "content-avatar.zip\r\n961cd6b55ab23d2efcc9a7319191aa37\r\n293687\r\n1020627\r\n" +
        "content-configs.zip\r\nd78901fd45a60d4a363a9bd23de96d4f\r\n306409\r\n1804944\r\n";

    [Fact]
    public void ParsesManifest_IntoOrderedEntries()
    {
        var entries = RddManifestParser.Parse(RealManifest);

        Assert.Equal(3, entries.Count);
        Assert.Equal("RobloxApp.zip", entries[0].FileName);
        Assert.Equal("aeaa4432a6005a7cead977db05447511", entries[0].Md5);
        Assert.Equal(134901428, entries[0].UncompressedSize);
        Assert.Equal(173551147, entries[0].CompressedSize);

        Assert.Equal("content-avatar.zip", entries[1].FileName);
        Assert.Equal("content-configs.zip", entries[2].FileName);
        Assert.Equal("d78901fd45a60d4a363a9bd23de96d4f", entries[2].Md5);
    }

    [Fact]
    public void BuildsStandardFileUrls()
    {
        var guid = "version-d584fb6c717a43d9";
        Assert.Equal(
            $"https://setup.rbxcdn.com/{guid}-rbxPkgManifest.txt",
            string.Format(RddManifestParser.ManifestUrlFormat, guid));
        Assert.Equal(
            $"https://setup.rbxcdn.com/{guid}-content-configs.zip",
            string.Format(RddManifestParser.FileUrlFormat, guid, "content-configs.zip"));
    }

    [Fact]
    public void ParsesRealManifestFixture()
    {
        // Captured live from setup.rbxcdn.com/version-d584fb6c717a43d9-rbxPkgManifest.txt
        string manifest = File.ReadAllText(Path.Combine("Fixtures", "real-manifest.txt"));
        var entries = RddManifestParser.Parse(manifest);

        Assert.Equal(22, entries.Count);
        Assert.Equal("RobloxApp.zip", entries[0].FileName);
        Assert.Equal("aeaa4432a6005a7cead977db05447511", entries[0].Md5);
        Assert.Equal(134901428, entries[0].UncompressedSize);
        Assert.Contains(entries, e => e.FileName == "content-configs.zip" && e.Md5 == "d78901fd45a60d4a363a9bd23de96d4f");
        Assert.Contains(entries, e => e.FileName == "shaders.zip");
        Assert.Contains(entries, e => e.FileName == "content-sounds.zip");
    }
}

public class ExploitStatusTests
{
    private const string Current = "version-d584fb6c717a43d9";
    private const string Previous = "version-145f189a6a974303";

    [Fact]
    public void CurrentExploit_MapsToCurrent()
    {
        var e = new Exploit { RbxVersion = Current, Extype = "wexecutor" };
        Assert.Equal(ExploitStatus.Current, e.ComputeStatus(Current, Previous));
    }

    [Fact]
    public void StaleExploit_MapsToStale()
    {
        // Volt / Velocity / Ronin require the previous version.
        var e = new Exploit { RbxVersion = Previous, Extype = "wexecutor" };
        Assert.Equal(ExploitStatus.Stale, e.ComputeStatus(Current, Previous));
    }

    [Fact]
    public void OlderExploit_MapsToOld()
    {
        // Synapse Z requires a version older than the previous release.
        var e = new Exploit { RbxVersion = "version-36a2600cebf1487d", Extype = "wexecutor" };
        Assert.Equal(ExploitStatus.Old, e.ComputeStatus(Current, Previous));
    }

    [Fact]
    public void MissingRbxVersion_AssumesCurrent()
    {
        var e = new Exploit { RbxVersion = null, Extype = "wexternal" };
        Assert.Equal(ExploitStatus.Current, e.ComputeStatus(Current, Previous));
    }

    [Fact]
    public void UnknownCurrentVersion_AssumesCurrent()
    {
        // index.js getStatus: without a known current version nothing can be marked stale/old.
        var e = new Exploit { RbxVersion = "version-36a2600cebf1487d", Extype = "wexecutor" };
        Assert.Equal(ExploitStatus.Current, e.ComputeStatus(null, Previous));
    }
}

public class WeaoRddApiClientTests
{
    // Fixture mirroring /api/status/exploits: a hidden exploit, an unsupported extype
    // (aexecutor), and one entry per supported extype in shuffled index order.
    private const string ExploitsJson = """
        [
          { "title": "Synapse Z",  "version": "4.0", "rbxversion": "version-36a2600cebf1487d", "extype": "wexternal", "platform": "Windows", "hidden": false, "index": 2 },
          { "title": "Hidden",     "version": "1.0", "rbxversion": "version-hidden",           "extype": "wexecutor", "platform": "Windows", "hidden": true,  "index": 0 },
          { "title": "Android",    "version": "1.0", "rbxversion": "version-android",          "extype": "aexecutor", "platform": "Android", "hidden": false, "index": 1 },
          { "title": "Potassium",  "version": "1.2", "rbxversion": "version-d584fb6c717a43d9", "extype": "wexecutor", "platform": "Windows", "hidden": false, "index": 1 },
          { "title": "Mac Exec",   "version": "2.1", "rbxversion": "version-mac",              "extype": "mexecutor", "platform": "Mac",     "hidden": false, "index": 3 },
          { "title": "No Index",   "version": "0.9", "rbxversion": "version-noindex",          "extype": "wexecutor", "platform": "Windows", "hidden": false }
        ]
        """;

    private static WeaoRddApiClient ClientReturning(string json) =>
        // useCache: false — these tests pin filtering/sorting behavior and must not read the
        // shared static cache populated by other tests.
        new(useCache: false, http: new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        })));

    [Fact]
    public async Task FiltersAndSorts_LikeReferencePage()
    {
        var exploits = await ClientReturning(ExploitsJson).GetExploitsAsync();

        // Hidden + non-TYPES (aexecutor) dropped; order is executor class (wexecutor →
        // wexternal → mexecutor) then index, with missing index sorting last (99).
        Assert.Equal(
            new[] { "Potassium", "No Index", "Synapse Z", "Mac Exec" },
            exploits.Select(e => e.Title));
    }

    [Fact]
    public async Task MapsDeploymentVersion_FromRbxVersion()
    {
        var exploits = await ClientReturning(ExploitsJson).GetExploitsAsync();
        var potassium = exploits.First(e => e.Title == "Potassium");

        // The reference page reads the deploy hash from "rbxversion", never "version".
        Assert.Equal("version-d584fb6c717a43d9", potassium.RbxVersion);
        Assert.Null(exploits.First(e => e.Title == "No Index").Index);
    }

    [Fact]
    public async Task FreshCache_ServesWithoutAnotherNetworkHit()
    {
        int calls = 0;
        var client = new WeaoRddApiClient(
            baseUrl: $"https://cache-{Guid.NewGuid():N}.test",
            http: new HttpClient(new FakeHttpHandler(_ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ExploitsJson) };
            })));

        var first = await client.GetExploitsAsync();
        var second = await client.GetExploitsAsync();

        Assert.Equal(4, first.Count);
        Assert.Equal(4, second.Count);
        Assert.Equal(1, calls); // second call served entirely from cache
    }

    [Fact]
    public async Task StaleCache_IsServed_WhenNetworkFails()
    {
        int calls = 0;
        var client = new WeaoRddApiClient(
            baseUrl: $"https://cache-{Guid.NewGuid():N}.test",
            cacheTtl: TimeSpan.Zero, // always stale after the first fetch
            http: new HttpClient(new FakeHttpHandler(_ =>
                ++calls == 1
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ExploitsJson) }
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        var first = await client.GetExploitsAsync();
        // Second call: the entry is stale, the network is down — the last-known-good list
        // must be served (the RDD page stays populated offline) instead of throwing.
        var second = await client.GetExploitsAsync();

        Assert.Equal(first.Select(e => e.Title), second.Select(e => e.Title));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task StaleCache_RevalidatesInBackground_AndServesUpdatedData()
    {
        int calls = 0;
        var client = new WeaoRddApiClient(
            baseUrl: $"https://cache-{Guid.NewGuid():N}.test",
            cacheTtl: TimeSpan.Zero,
            http: new HttpClient(new FakeHttpHandler(_ =>
            {
                calls++;
                string json = calls == 1 ? ExploitsJson : ExploitsJson.Replace("Synapse Z", "Synapse Z v2");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            })));

        var first = await client.GetExploitsAsync();
        Assert.Contains(first, e => e.Title == "Synapse Z");

        // Stale-while-revalidate: this call serves the stale list immediately and refreshes
        // in the background; poll until the revalidated payload lands (the list is sorted, so
        // check membership — "Synapse Z v2" never sorts first).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        bool sawUpdated = false;
        while (DateTime.UtcNow < deadline)
        {
            sawUpdated = (await client.GetExploitsAsync()).Any(e => e.Title == "Synapse Z v2");
            if (sawUpdated) break;
            await Task.Delay(25);
        }

        Assert.True(sawUpdated, "background revalidation never replaced the stale cache entry");
    }

}

/// <summary>Shared fake HTTP handler used across RDD and update tests.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_respond(request));
}

/// <summary>
/// Fake handler with an async respond — lets tests observe genuinely overlapping requests
/// (the synchronous fake above completes inline, which would hide real parallelism).
/// </summary>
internal sealed class FakeAsyncHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

    public FakeAsyncHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => await _respond(request).ConfigureAwait(false);
}

/// <summary>Builds an in-memory Roblox deployment CDN (manifests + zips) for tests.</summary>
internal static class FakeRddCdn
{
    public static string Manifest(params (string File, byte[] Content, long Uncompressed, long Compressed)[] files)
    {
        var sb = new StringBuilder();
        sb.AppendLine("v0");
        foreach (var (file, content, uncompressed, compressed) in files)
        {
            sb.AppendLine(file);
            sb.AppendLine(Md5(content)); // mirrors the real manifest: MD5 of the downloaded archive
            sb.AppendLine(uncompressed.ToString());
            sb.AppendLine(compressed.ToString());
        }
        return sb.ToString();
    }

    public static byte[] Zip(params (string Name, byte[] Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = zip.CreateEntry(name);
                using var w = entry.Open();
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static string Md5(byte[] value) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(value)).ToLowerInvariant();

    /// <summary>Serves manifests + zips for any number of deployments keyed by version GUID.</summary>
    public static HttpClient Client(params (string Guid, string Manifest, Dictionary<string, byte[]> Files)[] deployments)
        => Client(liveVersion: null, deployments);

    /// <summary>
    /// Serves manifests + zips for any number of deployments, plus the client-version
    /// endpoint (the "live build") when <paramref name="liveVersion"/> is provided.
    /// </summary>
    public static HttpClient Client(string? liveVersion, params (string Guid, string Manifest, Dictionary<string, byte[]> Files)[] deployments)
    {
        var handler = new FakeHttpHandler(request =>
        {
            string url = request.RequestUri!.ToString();
            if (liveVersion is not null && url.Contains("client-version/WindowsPlayer", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"clientVersionUpload\":\"{liveVersion}\"}}")
                };
            foreach (var (guid, manifest, files) in deployments)
            {
                if (url.EndsWith($"{guid}-rbxPkgManifest.txt", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) };
                foreach (var (file, bytes) in files)
                    if (url.EndsWith($"{guid}-{file}", StringComparison.Ordinal))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        return new HttpClient(handler);
    }
}

public class RddInstallTests
{
    private const string VersionA = "version-aaaa000000000001";
    private const string VersionB = "version-bbbb000000000002";

    private static (string Manifest, Dictionary<string, byte[]> Files) Deployment(string version, string playerContent = "player")
    {
        // Manifest sizes must match the actual zips so cumulative progress stays honest.
        byte[] appZip = FakeRddCdn.Zip(
            ("RobloxPlayerBeta.exe", Encoding.UTF8.GetBytes(playerContent)),
            ("content/config.ini", Encoding.UTF8.GetBytes("k=v")));
        byte[] cfgZip = FakeRddCdn.Zip(("extra.txt", Encoding.UTF8.GetBytes("extra")));
        var files = new Dictionary<string, byte[]>
        {
            ["RobloxApp.zip"] = appZip,
            ["content-configs.zip"] = cfgZip
        };
        var manifest = FakeRddCdn.Manifest(
            ("RobloxApp.zip", appZip, appZip.Length * 3, appZip.Length),
            ("content-configs.zip", cfgZip, cfgZip.Length * 3, cfgZip.Length));
        return (manifest, files);
    }

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), $"rddinstall_{Guid.NewGuid():N}");

    private static Exploit Pinned(string tag, string version) =>
        new() { Title = tag, RbxVersion = version, Extype = "wexecutor" };

    [Fact]
    public async Task InstallAsync_StreamsAndExtracts_Tagged()
    {
        var (manifest, files) = Deployment(VersionA, "hello-rdd");
        var service = new RobloxDeploymentService(FakeRddCdn.Client((VersionA, manifest, files)));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            var progress = new List<InstallProgress>();
            string folder = await service.InstallAsync(VersionA, root, "Potassium", new Progress<InstallProgress>(progress.Add));

            Assert.Equal(Path.Combine(root, VersionA), folder);
            Assert.Equal("hello-rdd", File.ReadAllText(Path.Combine(folder, "RobloxPlayerBeta.exe")));
            Assert.Equal("k=v", File.ReadAllText(Path.Combine(folder, "content", "config.ini")));
            Assert.Equal("Potassium", File.ReadAllText(Path.Combine(folder, RobloxDeploymentService.TagFileName)));

            Assert.Contains(progress, p => p.Phase == InstallPhase.Downloading && p.FileName == "RobloxApp.zip");
            Assert.Contains(progress, p => p.Phase == InstallPhase.Extracting && p.FileName == "content-configs.zip");
            Assert.Equal(InstallPhase.Done, progress[^1].Phase);
            Assert.All(progress.Where(p => p.Phase == InstallPhase.Downloading && p.BytesTotal > 0),
                p => Assert.True(p.BytesDone <= p.BytesTotal));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallAsync_CopiesRawNonZipEntries_Verbatim()
    {
        // Real deployments ship raw files (e.g. RobloxPlayerInstaller.exe) that are not zips —
        // they must land in the version folder as-is, not be extracted (which would throw
        // "End of Central Directory record could not be found").
        byte[] installer = Encoding.UTF8.GetBytes("MZ fake installer payload");
        byte[] appZip = FakeRddCdn.Zip(("RobloxPlayerBeta.exe", Encoding.UTF8.GetBytes("player")));
        var files = new Dictionary<string, byte[]>
        {
            ["RobloxApp.zip"] = appZip,
            ["RobloxPlayerInstaller.exe"] = installer
        };
        var manifest = FakeRddCdn.Manifest(
            ("RobloxApp.zip", appZip, appZip.Length * 3, appZip.Length),
            ("RobloxPlayerInstaller.exe", installer, installer.Length, installer.Length));

        var service = new RobloxDeploymentService(FakeRddCdn.Client((VersionA, manifest, files)));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            string folder = await service.InstallAsync(VersionA, root, "Default");

            Assert.Equal("player", File.ReadAllText(Path.Combine(folder, "RobloxPlayerBeta.exe")));
            Assert.Equal("MZ fake installer payload", File.ReadAllText(Path.Combine(folder, "RobloxPlayerInstaller.exe")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallAsync_LeavesNoStagingOrStaleFolders_OnSuccess()
    {
        var (manifest, files) = Deployment(VersionA);
        var manager = new InstallManager(new RobloxDeploymentService(FakeRddCdn.Client((VersionA, manifest, files))));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            var result = await manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: false);

            Assert.Equal(InstallResultKind.Installed, result.Kind);
            // Staging/swap is fully cleaned up — only the real version folder remains.
            Assert.Single(Directory.GetDirectories(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallManager_ForceFailure_LeavesPreviousInstallIntact()
    {
        // The key staging guarantee: a re-download that fails mid-way (checksum mismatch)
        // must never damage the already-installed copy — unlike the old force mode, which
        // deleted it before downloading.
        var (manifest, files) = Deployment(VersionA, "good");
        var manager = new InstallManager(new RobloxDeploymentService(FakeRddCdn.Client((VersionA, manifest, files))));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            var first = await manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: false);
            Assert.Equal(InstallResultKind.Installed, first.Kind);

            files["RobloxApp.zip"] = Encoding.UTF8.GetBytes("tampered-bytes"); // breaks the manifest MD5
            var second = await manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: true);

            Assert.Equal(InstallResultKind.Failed, second.Kind);
            Assert.Contains("Checksum mismatch", second.Message);
            Assert.True(Directory.Exists(first.VersionFolder!), "previous install must survive a failed re-download");
            Assert.Equal("good", File.ReadAllText(Path.Combine(first.VersionFolder!, "RobloxPlayerBeta.exe")));
            var leftover = Directory.GetDirectories(root).Select(Path.GetFileName).ToArray();
            Assert.True(leftover.Length == 1, $"staging folder should be wiped on failure, found: {string.Join(", ", leftover)}");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallManager_ForceCancel_LeavesPreviousInstallIntact()
    {
        var (manifest, files) = Deployment(VersionA, "good");
        var manager = new InstallManager(new RobloxDeploymentService(FakeRddCdn.Client((VersionA, manifest, files))));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            var first = await manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: false);
            Assert.Equal(InstallResultKind.Installed, first.Kind);

            // Second run: manifest is served, but the file download blocks until cancelled.
            var cts = new CancellationTokenSource();
            var blocking = new HttpClient(new FakeHttpHandler(request =>
            {
                string url = request.RequestUri!.ToString();
                if (url.EndsWith("-rbxPkgManifest.txt"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent(cts.Token) };
            }));
            var reinstall = new InstallManager(new RobloxDeploymentService(blocking));

            var task = reinstall.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: true, ct: cts.Token);
            await Task.Delay(150); // let it reach the blocking download
            cts.Cancel();
            var second = await task;

            Assert.Equal(InstallResultKind.Cancelled, second.Kind);
            Assert.True(Directory.Exists(first.VersionFolder!), "previous install must survive a cancelled re-download");
            Assert.Equal("good", File.ReadAllText(Path.Combine(first.VersionFolder!, "RobloxPlayerBeta.exe")));
            var leftover = Directory.GetDirectories(root).Select(Path.GetFileName).ToArray();
            Assert.True(leftover.Length == 1, $"staging folder should be wiped on cancel, found: {string.Join(", ", leftover)}");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallManager_ForceReinstall_SwapsWithoutStaleFolder()
    {
        var (manifest, files) = Deployment(VersionA, "good");
        var manager = new InstallManager(new RobloxDeploymentService(FakeRddCdn.Client((VersionA, manifest, files))));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            var first = await manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: false);
            Assert.Equal(InstallResultKind.Installed, first.Kind);

            File.WriteAllText(Path.Combine(first.VersionFolder!, "RobloxPlayerBeta.exe"), "broken");
            var second = await manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: true);

            Assert.Equal(InstallResultKind.Installed, second.Kind);
            var leftover = Directory.GetDirectories(root).Select(Path.GetFileName).ToArray();
            Assert.True(leftover.Length == 1, $"swap must clean up the old-aside folder, found: {string.Join(", ", leftover)}");
            Assert.Equal("good", File.ReadAllText(Path.Combine(first.VersionFolder!, "RobloxPlayerBeta.exe")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallAsync_SerialMode_StillInstalls()
    {
        var (manifest, files) = Deployment(VersionA, "serial-player");
        var service = new RobloxDeploymentService(FakeRddCdn.Client((VersionA, manifest, files)));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            string folder = await service.InstallAsync(VersionA, root, "Default", progress: null, ct: default, parallelDownloads: false);

            Assert.Equal("serial-player", File.ReadAllText(Path.Combine(folder, "RobloxPlayerBeta.exe")));
            Assert.Equal("Default", File.ReadAllText(Path.Combine(folder, RobloxDeploymentService.TagFileName)));
            Assert.Single(Directory.GetDirectories(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallAsync_DownloadsFilesInParallel()
    {
        // A handler that tracks how many requests are in flight: with the reference page's
        // "Parallel Downloads" default on, several manifest files must download at once.
        var files = new Dictionary<string, byte[]>();
        var manifestEntries = new List<(string File, byte[] Content, long Uncompressed, long Compressed)>();
        for (int i = 0; i < 6; i++)
        {
            byte[] zip = FakeRddCdn.Zip(($"file-{i}.txt", Encoding.UTF8.GetBytes($"content-{i}")));
            files[$"part-{i}.zip"] = zip;
            manifestEntries.Add(($"part-{i}.zip", zip, zip.Length, zip.Length));
        }
        string manifest = FakeRddCdn.Manifest(manifestEntries.ToArray());

        int active = 0;
        int maxActive = 0;
        var handler = new FakeAsyncHttpHandler(async request =>
        {
            int now = Interlocked.Increment(ref active);
            try
            {
                int observed;
                while ((observed = Volatile.Read(ref maxActive)) < now &&
                       Interlocked.CompareExchange(ref maxActive, now, observed) != observed)
                { }

                // Keep the request in flight long enough for the other gate slots to overlap.
                await Task.Delay(50);

                string url = request.RequestUri!.ToString();
                if (url.EndsWith($"{VersionA}-rbxPkgManifest.txt", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) };
                foreach (var (file, bytes) in files)
                    if (url.EndsWith(file, StringComparison.Ordinal))
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        var service = new RobloxDeploymentService(new HttpClient(handler));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            await service.InstallAsync(VersionA, root, "Default");

            for (int i = 0; i < 6; i++)
                Assert.Equal($"content-{i}", File.ReadAllText(Path.Combine(root, VersionA, $"file-{i}.txt")));
            Assert.True(Volatile.Read(ref maxActive) >= 2,
                $"expected concurrent downloads, saw at most {Volatile.Read(ref maxActive)} in flight");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallManager_SkipsSameTagAndVersion()
    {
        var (manifest, files) = Deployment(VersionA);
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        string folder = Path.Combine(root, VersionA);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, RobloxDeploymentService.TagFileName), "Potassium");
        try
        {
            var manager = new InstallManager(new RobloxDeploymentService(FakeRddCdn.Client((VersionA, manifest, files))));
            var result = await manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: false);

            Assert.Equal(InstallResultKind.Skipped, result.Kind);
            Assert.Equal(folder, result.VersionFolder);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallManager_ForceReinstallsOverExistingFolder()
    {
        var (manifest, files) = Deployment(VersionA, "good");
        var manager = new InstallManager(new RobloxDeploymentService(FakeRddCdn.Client((VersionA, manifest, files))));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            var first = await manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: false);
            Assert.Equal(InstallResultKind.Installed, first.Kind);
            string exe = Path.Combine(first.VersionFolder!, "RobloxPlayerBeta.exe");

            // Corrupt a file, then repair via force (re-download).
            File.WriteAllText(exe, "broken");
            var second = await manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: true);

            Assert.Equal(InstallResultKind.Installed, second.Kind);
            Assert.Equal("good", File.ReadAllText(exe));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallManager_NewVersionSupersedesOldTaggedFolder()
    {
        var (manifestA, filesA) = Deployment(VersionA);
        var (manifestB, filesB) = Deployment(VersionB);
        var manager = new InstallManager(new RobloxDeploymentService(
            FakeRddCdn.Client((VersionA, manifestA, filesA), (VersionB, manifestB, filesB))));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            var first = await manager.InstallAsync(root, "Default", Pinned("Default", VersionA), force: false);
            Assert.Equal(InstallResultKind.Installed, first.Kind);
            Assert.True(Directory.Exists(Path.Combine(root, VersionA)));

            // A newer live version under the same tag supersedes the old folder.
            var second = await manager.InstallAsync(root, "Default", Pinned("Default", VersionB), force: false);

            Assert.Equal(InstallResultKind.Installed, second.Kind);
            Assert.Equal(Path.Combine(root, VersionB), second.VersionFolder);
            Assert.False(Directory.Exists(Path.Combine(root, VersionA)), "superseded folder should be removed");
            Assert.Equal("Default", File.ReadAllText(Path.Combine(root, VersionB, RobloxDeploymentService.TagFileName)));
            Assert.Single(new RddDeploymentStore(root).ListInstalls());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallManager_CorruptDownload_FailsAndCleansUp()
    {
        // Tamper with a served file AFTER the manifest was built: the manifest MD5 no
        // longer matches, so the install must fail instead of extracting a broken tree.
        var (manifest, files) = Deployment(VersionA, "good");
        files["RobloxApp.zip"] = Encoding.UTF8.GetBytes("tampered-bytes");

        var manager = new InstallManager(new RobloxDeploymentService(FakeRddCdn.Client((VersionA, manifest, files))));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            var result = await manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: false);

            Assert.Equal(InstallResultKind.Failed, result.Kind);
            Assert.Contains("Checksum mismatch", result.Message);
            Assert.False(Directory.Exists(Path.Combine(root, VersionA)), "partial folder should be wiped on checksum failure");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task InstallManager_CancelCleansUpPartialFolder()
    {
        var cts = new CancellationTokenSource();
        var handler = new FakeHttpHandler(request =>
        {
            string url = request.RequestUri!.ToString();
            if (url.EndsWith("-rbxPkgManifest.txt"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(FakeRddCdn.Manifest(("RobloxApp.zip", Array.Empty<byte>(), 123, 100)))
                };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new BlockingContent(cts.Token) };
        });

        var manager = new InstallManager(new RobloxDeploymentService(new HttpClient(handler)));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            var task = manager.InstallAsync(root, "Potassium", Pinned("Potassium", VersionA), force: false, ct: cts.Token);
            await Task.Delay(150); // let it reach the blocking download
            cts.Cancel();

            var result = await task;
            Assert.Equal(InstallResultKind.Cancelled, result.Kind);
            Assert.False(Directory.Exists(Path.Combine(root, VersionA)), "partial folder should be wiped on cancel");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    /// <summary>Content whose stream never yields until the token is cancelled.</summary>
    private sealed class BlockingContent : HttpContent
    {
        private readonly CancellationToken _token;

        public BlockingContent(CancellationToken token) => _token = token;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BlockingStream(_token));

        protected override bool TryComputeLength(out long length)
        {
            length = 1000;
            return true;
        }
    }

    private sealed class BlockingStream : Stream
    {
        private readonly CancellationToken _token;

        public BlockingStream(CancellationToken token) => _token = token;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            return 0;
        }
    }
}

public class RddDeploymentStoreTests
{
    private const string VersionDefault = "version-d584fb6c717a43d9";
    private const string VersionPotassium = "version-aaaa000000000001";

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rddstore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateInstall(string root, string version, string? tag)
    {
        var folder = Path.Combine(root, version);
        Directory.CreateDirectory(folder);
        if (tag is not null)
            File.WriteAllText(Path.Combine(folder, RddDeploymentStore.TagFileName), tag);
        return folder;
    }

    [Fact]
    public void TagsAndLocatesInstalls()
    {
        string root = NewTempRoot();
        try
        {
            var store = new RddDeploymentStore(root);

            var folder = Path.Combine(root, VersionPotassium);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, RddDeploymentStore.TagFileName), "Potassium");

            Assert.Equal(folder, store.LocateTagged("Potassium"));
            Assert.Equal(folder, store.LocateVersionFolder());
            Assert.Null(store.LocateTagged("NoSuchExploit"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LocateActive_ExactFolderName_Wins()
    {
        string root = NewTempRoot();
        try
        {
            var store = new RddDeploymentStore(root);
            var defaultFolder = CreateInstall(root, VersionDefault, "Default");
            var potassiumFolder = CreateInstall(root, VersionPotassium, "Potassium");

            // An exact folder-name match beats even the Default-tagged preference.
            Assert.Equal(potassiumFolder, store.LocateActive(VersionPotassium));
            Assert.Equal(defaultFolder, store.LocateActive(VersionDefault));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LocateActive_TagMatch_SurvivesFolderRename()
    {
        // The active value stores the tag when the install is tagged; a re-download to a
        // new version changes the folder name but keeps the tag, so resolution must follow
        // the tag — not the (now-gone) folder name.
        string root = NewTempRoot();
        try
        {
            var store = new RddDeploymentStore(root);
            CreateInstall(root, VersionDefault, "Default");
            var potassiumFolder = CreateInstall(root, "version-bbbb000000000002", "Potassium");

            Assert.Equal(potassiumFolder, store.LocateActive("Potassium"));
            Assert.True(store.ActiveKeyResolves("Potassium"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LocateActive_FallsBackToDefault_WhenStoredValueGone()
    {
        string root = NewTempRoot();
        try
        {
            var store = new RddDeploymentStore(root);
            var defaultFolder = CreateInstall(root, VersionDefault, "Default");

            Assert.Equal(defaultFolder, store.LocateActive("version-ghost000000000000"));
            Assert.Equal(defaultFolder, store.LocateActive("GhostTag"));
            Assert.False(store.ActiveKeyResolves("GhostTag"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LocateActive_FallsBackToNewest_WhenNoDefault()
    {
        string root = NewTempRoot();
        try
        {
            var store = new RddDeploymentStore(root);
            var folder = CreateInstall(root, VersionPotassium, "Potassium");

            Assert.Equal(folder, store.LocateActive(""));
            Assert.Equal(folder, store.LocateActive(null));
            Assert.Equal(folder, store.LocateActive("GhostTag"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LocateActive_ReturnsNull_WhenNothingInstalled()
    {
        string root = NewTempRoot();
        try
        {
            var store = new RddDeploymentStore(root);
            Assert.Null(store.LocateActive("Anything"));
            Assert.Null(store.LocateActive(null));
            Assert.False(store.ActiveKeyResolves("Anything"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

public class RddActiveInstallTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rddactive_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (SettingsStore Settings, string Root) NewSettingsAndRoot()
    {
        string dir = NewTempDir();
        return (new SettingsStore(Path.Combine(dir, "settings.ini")), dir);
    }

    private static string CreateInstall(string root, string version, string? tag)
    {
        var folder = Path.Combine(root, version);
        Directory.CreateDirectory(folder);
        if (tag is not null)
            File.WriteAllText(Path.Combine(folder, RddDeploymentStore.TagFileName), tag);
        return folder;
    }

    [Fact]
    public void Resolve_PersistsFallback_WhenStoredInstallDeleted()
    {
        var (settings, root) = NewSettingsAndRoot();
        try
        {
            settings.Set(RddOptions.ActiveInstallKey, "version-ghost000000000000");
            settings.Save();
            var defaultFolder = CreateInstall(root, "version-d584fb6c717a43d9", "Default");

            string? resolved = RddActiveInstall.Resolve(settings, new RddDeploymentStore(root));

            Assert.Equal(defaultFolder, resolved);
            // The fallback is persisted tag-first (Set prefers the tag), so the key now
            // names the Default install by its tag.
            Assert.Equal("Default", settings.Get(RddOptions.ActiveInstallKey, ""));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Resolve_DoesNotRewrite_WhenStoredValueStillResolves()
    {
        var (settings, root) = NewSettingsAndRoot();
        try
        {
            settings.Set(RddOptions.ActiveInstallKey, "Potassium");
            settings.Save();
            var potassiumFolder = CreateInstall(root, "version-aaaa000000000001", "Potassium");

            string? resolved = RddActiveInstall.Resolve(settings, new RddDeploymentStore(root));

            Assert.Equal(potassiumFolder, resolved);
            Assert.Equal("Potassium", settings.Get(RddOptions.ActiveInstallKey, ""));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Resolve_DoesNotPersist_WhenNothingChosen()
    {
        // A user who never picked an active install keeps the key unset; resolution still
        // falls back to the Default-tagged install without writing settings.
        var (settings, root) = NewSettingsAndRoot();
        try
        {
            var defaultFolder = CreateInstall(root, "version-d584fb6c717a43d9", "Default");

            string? resolved = RddActiveInstall.Resolve(settings, new RddDeploymentStore(root));

            Assert.Equal(defaultFolder, resolved);
            Assert.Equal("", settings.Get(RddOptions.ActiveInstallKey, ""));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Set_StoresTag_ForTaggedInstall()
    {
        var (settings, root) = NewSettingsAndRoot();
        try
        {
            var folder = CreateInstall(root, "version-aaaa000000000001", "Potassium");
            RddActiveInstall.Set(settings, folder);
            Assert.Equal("Potassium", settings.Get(RddOptions.ActiveInstallKey, ""));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Set_StoresFolderName_ForUntaggedInstall()
    {
        var (settings, root) = NewSettingsAndRoot();
        try
        {
            var folder = CreateInstall(root, "version-cccc000000000003", tag: null);
            RddActiveInstall.Set(settings, folder);
            Assert.Equal("version-cccc000000000003", settings.Get(RddOptions.ActiveInstallKey, ""));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

public class ClientUpdaterTests
{
    private const string VersionA = "version-aaaa000000000001";
    private const string VersionB = "version-bbbb000000000002";

    private static (string Manifest, Dictionary<string, byte[]> Files) Deployment(string version)
    {
        // Same shape as RddInstallTests.Deployment: a player exe (so the settings patcher
        // accepts the folder) + a config zip, with honest size values.
        byte[] appZip = FakeRddCdn.Zip(
            ("RobloxPlayerBeta.exe", Encoding.UTF8.GetBytes("player-" + version)),
            ("content/config.ini", Encoding.UTF8.GetBytes("k=v")));
        byte[] cfgZip = FakeRddCdn.Zip(("extra.txt", Encoding.UTF8.GetBytes("extra")));
        var files = new Dictionary<string, byte[]>
        {
            ["RobloxApp.zip"] = appZip,
            ["content-configs.zip"] = cfgZip
        };
        var manifest = FakeRddCdn.Manifest(
            ("RobloxApp.zip", appZip, appZip.Length * 3, appZip.Length),
            ("content-configs.zip", cfgZip, cfgZip.Length * 3, cfgZip.Length));
        return (manifest, files);
    }

    private static string NewTempDir() => Path.Combine(Path.GetTempPath(), $"clientupd_{Guid.NewGuid():N}");

    private static string CreateDefaultInstall(string root, string version)
    {
        // A real deployment carries the player exe — the settings patcher requires it.
        var folder = Path.Combine(root, version);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, RobloxDeploymentService.TagFileName), "Default");
        File.WriteAllText(Path.Combine(folder, "RobloxPlayerBeta.exe"), "existing-player");
        return folder;
    }

    [Fact]
    public async Task CheckAsync_ReturnsNull_WhenNoDefaultInstall()
    {
        var (manifest, files) = Deployment(VersionA);
        var updater = new ClientUpdater(new RobloxDeploymentService(FakeRddCdn.Client(VersionA, (VersionA, manifest, files))));
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(await updater.CheckAsync(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CheckAsync_ReportsNewerBuild_WhenLiveDiffers()
    {
        var (manifestA, filesA) = Deployment(VersionA);
        var (manifestB, filesB) = Deployment(VersionB);
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            CreateDefaultInstall(root, VersionA);
            var updater = new ClientUpdater(new RobloxDeploymentService(
                FakeRddCdn.Client(VersionB, (VersionA, manifestA, filesA), (VersionB, manifestB, filesB))));

            var info = await updater.CheckAsync(root);

            Assert.NotNull(info);
            Assert.Equal(VersionA, info!.CurrentVersion);
            Assert.Equal(VersionB, info.LiveVersion);
            Assert.True(info.Available);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CheckAsync_ReportsCurrent_WhenLiveMatches()
    {
        var (manifestA, filesA) = Deployment(VersionA);
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            CreateDefaultInstall(root, VersionA);
            var updater = new ClientUpdater(new RobloxDeploymentService(
                FakeRddCdn.Client(VersionA, (VersionA, manifestA, filesA))));

            var info = await updater.CheckAsync(root);

            Assert.NotNull(info);
            Assert.False(info!.Available);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UpdateAsync_InstallsNewBuild_AndReappliesSettings()
    {
        var (manifestA, filesA) = Deployment(VersionA);
        var (manifestB, filesB) = Deployment(VersionB);
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            CreateDefaultInstall(root, VersionA);
            var settings = new SettingsStore(Path.Combine(root, "settings.ini"));
            settings.Set("UnlockFPS", "true");
            settings.Set("MaxFPSValue", "360");
            settings.Save();
            var fastFlags = new FastFlagStore(Path.Combine(root, "FastFlags.json"));
            fastFlags.Set("FFlagHandleAltEnterFullscreenManually", "true");

            var updater = new ClientUpdater(new RobloxDeploymentService(
                FakeRddCdn.Client(VersionB, (VersionA, manifestA, filesA), (VersionB, manifestB, filesB))));

            var result = await updater.UpdateAsync(root, settings, fastFlags);

            Assert.Equal(InstallResultKind.Installed, result.Install.Kind);
            Assert.True(result.SettingsApplied);
            Assert.True(Directory.Exists(Path.Combine(root, VersionB)), "new Default folder should exist");
            Assert.False(Directory.Exists(Path.Combine(root, VersionA)), "superseded Default folder should be removed");

            // The fresh install must carry the user's FPS unlock + fast flags.
            string settingsFile = Path.Combine(root, VersionB, "ClientSettings", ClientSettingsPatcher.SettingsFileName);
            var patched = JObject.Parse(File.ReadAllText(settingsFile));
            Assert.Equal(360L, patched["DFIntTaskSchedulerTargetFps"]!.Value<long>());
            Assert.True(patched["FFlagHandleAltEnterFullscreenManually"]!.Value<bool>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task UpdateAsync_SkipsWhenAlreadyCurrent_StillReappliesSettings()
    {
        var (manifestA, filesA) = Deployment(VersionA);
        string root = NewTempDir();
        Directory.CreateDirectory(root);
        try
        {
            CreateDefaultInstall(root, VersionA);
            var settings = new SettingsStore(Path.Combine(root, "settings.ini"));
            settings.Set("UnlockFPS", "true");
            settings.Save();
            var fastFlags = new FastFlagStore(Path.Combine(root, "FastFlags.json"));

            var updater = new ClientUpdater(new RobloxDeploymentService(
                FakeRddCdn.Client(VersionA, (VersionA, manifestA, filesA))));

            var result = await updater.UpdateAsync(root, settings, fastFlags);

            Assert.Equal(InstallResultKind.Skipped, result.Install.Kind);
            Assert.True(result.SettingsApplied);

            // The existing install is patched in place, with the default max FPS (no value set).
            string settingsFile = Path.Combine(root, VersionA, "ClientSettings", ClientSettingsPatcher.SettingsFileName);
            var patched = JObject.Parse(File.ReadAllText(settingsFile));
            Assert.Equal(ClientSettingsPatcher.DefaultMaxFps, patched["DFIntTaskSchedulerTargetFps"]!.Value<long>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
