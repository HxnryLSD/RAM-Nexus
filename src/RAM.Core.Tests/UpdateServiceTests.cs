using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using RAM.Core.Updates;

namespace RAM.Core.Tests;

public class UpdateServiceTests
{
    private const string ReleaseJson = """
        {
          "tag_name": "v9.9.9",
          "assets": [ { "browser_download_url": "https://example.com/ram-v9.9.9.zip" } ]
        }
        """;

    private static UpdateService ServiceReturning(string json) =>
        new(manifestUrl: "https://example.com/api/releases/latest",
            http: new HttpClient(new FakeHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) })));

    [Fact]
    public async Task CheckAsync_PrefersUpdateZipAsset_OverOtherAssets()
    {
        // Our releases carry Setup.exe + portable.zip + update.zip; the in-app updater must
        // pick the root-level update zip, not the wrapped portable one.
        const string multiAssetJson = """
            {
              "tag_name": "v9.9.9",
              "assets": [
                { "name": "RobloxAccountManager-Setup.exe", "browser_download_url": "https://example.com/setup.exe" },
                { "name": "RobloxAccountManager-portable.zip", "browser_download_url": "https://example.com/portable.zip" },
                { "name": "RobloxAccountManager-update.zip", "browser_download_url": "https://example.com/update.zip" }
              ]
            }
            """;

        var info = await ServiceReturning(multiAssetJson).CheckAsync(new Version(1, 0, 0));

        Assert.NotNull(info);
        Assert.Equal("https://example.com/update.zip", info!.DownloadUrl);
    }

    [Fact]
    public async Task StageUpdate_ExtractsZip_ToSiblingAppDir()
    {
        // Zip whose entries sit at the root, as the release pipeline builds it.
        string root = Path.Combine(Path.GetTempPath(), $"stage_{Guid.NewGuid():N}");
        string src = Path.Combine(root, "src");
        string zip = Path.Combine(root, "update.zip");
        string appDir = Path.Combine(root, "Roblox Account Manager");
        try
        {
            Directory.CreateDirectory(Path.Combine(src, "Views"));
            File.WriteAllText(Path.Combine(src, "Roblox Account Manager.exe"), "exe-bytes");
            File.WriteAllText(Path.Combine(src, "App.xbf"), "xbf-bytes");
            File.WriteAllText(Path.Combine(src, "Views", "AccountsPage.xbf"), "view-bytes");
            ZipFile.CreateFromDirectory(src, zip);

            string staged = await UpdateService.StageUpdateAsync(zip, "9.9.9", appDir);

            Assert.Equal(Path.Combine(root, "app-9.9.9"), staged);
            Assert.True(File.Exists(Path.Combine(staged, "Roblox Account Manager.exe")));
            Assert.True(File.Exists(Path.Combine(staged, "App.xbf")));
            Assert.True(File.Exists(Path.Combine(staged, "Views", "AccountsPage.xbf")));
            // The live app folder must never be touched by staging.
            Assert.False(Directory.Exists(appDir));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StageUpdate_ReplacesStaleStagedDir_AndRejectsZipMissingAppExe()
    {
        string root = Path.Combine(Path.GetTempPath(), $"stage_{Guid.NewGuid():N}");
        string src = Path.Combine(root, "src");
        string zip = Path.Combine(root, "update.zip");
        string appDir = Path.Combine(root, "Roblox Account Manager");
        string staged = Path.Combine(root, "app-9.9.9");
        try
        {
            // A stale staged dir from an aborted earlier attempt must be replaced, not mixed.
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "stale-file.txt"), "old");

            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "Roblox Account Manager.exe"), "exe-bytes");
            File.WriteAllText(Path.Combine(src, "new-file.txt"), "new");
            ZipFile.CreateFromDirectory(src, zip);

            string result = await UpdateService.StageUpdateAsync(zip, "9.9.9", appDir);

            Assert.Equal(staged, result);
            Assert.True(File.Exists(Path.Combine(staged, "new-file.txt")));
            Assert.False(File.Exists(Path.Combine(staged, "stale-file.txt")));

            // A package without the app exe at its root is not a valid update: rejected
            // before anything is written.
            string badSrc = Path.Combine(root, "bad-src");
            string badZip = Path.Combine(root, "bad.zip");
            Directory.CreateDirectory(Path.Combine(badSrc, "nope"));
            File.WriteAllText(Path.Combine(badSrc, "nope", "random.txt"), "x");
            ZipFile.CreateFromDirectory(badSrc, badZip);
            await Assert.ThrowsAsync<InvalidDataException>(() => UpdateService.StageUpdateAsync(badZip, "9.9.10", appDir));
            Assert.False(Directory.Exists(Path.Combine(root, "app-9.9.10")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SweepStaleUpdateArtifacts_DeletesOldDirs_KeepsPendingStagedDir()
    {
        string parent = Path.Combine(Path.GetTempPath(), $"sweep_{Guid.NewGuid():N}");
        string appDir = Path.Combine(parent, "Roblox Account Manager");
        try
        {
            Directory.CreateDirectory(appDir);
            string old1 = Path.Combine(parent, ".old-deadbeef");
            string old2 = Path.Combine(parent, ".old-cafebabe");
            string staged = Path.Combine(parent, "app-9.9.9");
            Directory.CreateDirectory(old1);
            Directory.CreateDirectory(old2);
            Directory.CreateDirectory(staged);

            UpdateService.SweepStaleUpdateArtifacts(appDir);

            Assert.False(Directory.Exists(old1));
            Assert.False(Directory.Exists(old2));
            // A staged update that was never applied is still pending — never swept.
            Assert.True(Directory.Exists(staged));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }
    }

    [Fact]
    public async Task CheckAsync_ReturnsUpdate_WhenNewer()
    {
        var info = await ServiceReturning(ReleaseJson).CheckAsync(new Version(1, 0, 0));

        Assert.NotNull(info);
        Assert.Equal("9.9.9", info!.Version);
        Assert.Equal("v9.9.9", info.TagName);
        Assert.Equal("https://example.com/ram-v9.9.9.zip", info.DownloadUrl);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNull_WhenUpToDate() =>
        Assert.Null(await ServiceReturning(ReleaseJson).CheckAsync(new Version(10, 0, 0)));

    [Fact]
    public async Task CheckAsync_PropagatesCancellation()
    {
        // A cancelled check must surface as cancellation, not masquerade as "no update".
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new UpdateService(manifestUrl: "https://example.com/x",
            http: new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckAsync(new Version(1, 0, 0), cts.Token));
    }

    [Fact]
    public async Task CheckAsync_ReturnsNull_WhenUnreachable()
    {
        var service = new UpdateService(manifestUrl: "https://example.com/x",
            http: new HttpClient(new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));

        Assert.Null(await service.CheckAsync(new Version(1, 0, 0)));
    }

    [Fact]
    public async Task DownloadAsync_Throws_WhenServerSendsFewerBytesThanDeclared()
    {
        byte[] payload = Encoding.UTF8.GetBytes("update-payload-bytes-truncated-download");
        var service = new UpdateService(manifestUrl: "https://example.com/x",
            http: new HttpClient(new FakeHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new TruncatedContent(payload) })));

        string dir = Path.Combine(Path.GetTempPath(), $"updatetest_{Guid.NewGuid():N}");
        try
        {
            var info = new UpdateInfo("v9.9.9", "9.9.9", "https://example.com/ram.zip");

            // A truncated download must fail (and leave no .zip) instead of being extracted
            // into a broken app folder on restart.
            await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(info, dir));
            Assert.False(File.Exists(Path.Combine(dir, "update-9.9.9.zip")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task DownloadAsync_WritesZip_WithByteProgress()
    {
        byte[] payload = Encoding.UTF8.GetBytes("update-payload-bytes");
        var service = new UpdateService(manifestUrl: "https://example.com/x",
            http: new HttpClient(new FakeHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) })));

        string dir = Path.Combine(Path.GetTempPath(), $"updatetest_{Guid.NewGuid():N}");
        try
        {
            var reports = new List<UpdateProgress>();
            var info = new UpdateInfo("v9.9.9", "9.9.9", "https://example.com/ram.zip");
            string zip = await service.DownloadAsync(info, dir, new Progress<UpdateProgress>(reports.Add));

            Assert.EndsWith("update-9.9.9.zip", zip);
            Assert.Equal(payload, File.ReadAllBytes(zip));
            Assert.NotEmpty(reports);
            Assert.Equal(payload.Length, reports[^1].BytesDone);
            Assert.Equal(payload.Length, reports[^1].BytesTotal);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    /// <summary>Content that declares a Content-Length larger than the bytes it actually sends.</summary>
    private sealed class TruncatedContent : HttpContent
    {
        private readonly byte[] _payload;

        public TruncatedContent(byte[] payload) => _payload = payload;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_payload, 0, _payload.Length / 2);

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }
    }
}
