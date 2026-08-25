using System.Net;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using RAM.Core.Roblox;

namespace RAM.Core.Tests;

public class ClientSettingsPatcherTests
{
    private static string MakeFakeInstall(string folderName = "version-test123")
    {
        var dir = Path.Combine(Path.GetTempPath(), folderName);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "RobloxPlayerLauncher.exe"), "fake");
        return dir;
    }

    [Fact]
    public void WritesFpsUnlock_IntoResolvedFolder()
    {
        var dir = MakeFakeInstall();
        try
        {
            Assert.True(ClientSettingsPatcher.PatchSettings(dir, unlockFps: true, maxFps: 300));

            var file = Path.Combine(dir, "ClientSettings", ClientSettingsPatcher.SettingsFileName);
            Assert.True(File.Exists(file));

            var json = JObject.Parse(File.ReadAllText(file));
            Assert.Equal(300, json["DFIntTaskSchedulerTargetFps"]!.Value<int>());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RejectsNonVersionFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"notversion_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(ClientSettingsPatcher.PatchSettings(dir, unlockFps: true));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CustomSettingsFile_TakesPrecedence()
    {
        var dir = MakeFakeInstall();
        var custom = Path.Combine(Path.GetTempPath(), $"custom_{Guid.NewGuid():N}.json");
        File.WriteAllText(custom, "{\"DFIntTaskSchedulerTargetFps\":999}");
        try
        {
            Assert.True(ClientSettingsPatcher.PatchSettings(dir, unlockFps: true, maxFps: 60, customSettingsPath: custom));
            var file = Path.Combine(dir, "ClientSettings", ClientSettingsPatcher.SettingsFileName);
            var json = JObject.Parse(File.ReadAllText(file));
            Assert.Equal(999, json["DFIntTaskSchedulerTargetFps"]!.Value<int>());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(custom)) File.Delete(custom);
        }
    }
}

public class RobloxLauncherTests
{
    private static string MakeInstall(params string[] exes)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"launch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        foreach (var exe in exes)
            File.WriteAllText(Path.Combine(dir, exe), "fake");
        return dir;
    }

    [Fact]
    public void ResolvePlayerExe_PrefersLauncher_FallsBackToBeta()
    {
        var both = MakeInstall("RobloxPlayerLauncher.exe", "RobloxPlayerBeta.exe");
        var betaOnly = MakeInstall("RobloxPlayerBeta.exe");
        var none = MakeInstall();
        try
        {
            Assert.Equal(Path.Combine(both, "RobloxPlayerLauncher.exe"), RobloxLauncher.ResolvePlayerExe(both));
            Assert.Equal(Path.Combine(betaOnly, "RobloxPlayerBeta.exe"), RobloxLauncher.ResolvePlayerExe(betaOnly));
            Assert.Null(RobloxLauncher.ResolvePlayerExe(none));
        }
        finally
        {
            Directory.Delete(both, true);
            Directory.Delete(betaOnly, true);
            Directory.Delete(none, true);
        }
    }

    [Fact]
    public void LaunchPlayer_ReturnsNull_WhenNoPlayerExe()
    {
        var dir = MakeInstall();
        try
        {
            var launcher = new RobloxLauncher();
            Assert.Null(launcher.LaunchPlayer(dir));
            Assert.Null(launcher.LaunchPlace(dir, 123));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BuildJoinscriptUrl_IncludesPlaceTrackerAndFlag()
    {
        var url = RobloxLauncher.BuildJoinscriptUrl(12345, browserTrackerId: "123456789012");

        Assert.StartsWith("https://assetgame.roblox.com/game/PlaceLauncher.ashx?", url);
        Assert.Contains("request=RequestGame", url);
        Assert.Contains("placeId=12345", url);
        Assert.Contains("browserTrackerId=123456789012", url);
        Assert.EndsWith("&isPlayTogetherGame=false", url);
        Assert.DoesNotContain("gameId", url);
    }

    [Fact]
    public void BuildJoinscriptUrl_UsesRequestGameJob_WhenJobGiven()
    {
        var url = RobloxLauncher.BuildJoinscriptUrl(12345, jobId: "abc-123", browserTrackerId: "42");

        Assert.Contains("request=RequestGameJob", url);
        Assert.Contains("gameId=abc-123", url);
    }

    [Fact]
    public void BuildAccountLaunchArgs_PassesTicketAndJoinscript()
    {
        string args = RobloxLauncher.BuildAccountLaunchArgs("ticket-xyz", 12345, browserTrackerId: "42");

        Assert.StartsWith("--app -t ticket-xyz -j \"https://assetgame.roblox.com/game/PlaceLauncher.ashx?", args);
        Assert.EndsWith("isPlayTogetherGame=false\"", args);
    }

    [Fact]
    public void LaunchPlaceAsAccount_ReturnsNull_WhenNoPlayerExe()
    {
        var dir = MakeInstall();
        try
        {
            var launcher = new RobloxLauncher();
            Assert.Null(launcher.LaunchPlaceAsAccount(dir, "ticket-xyz", 123));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

public class RobloxApiClientTests
{
    private static RobloxApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond, string? cookie = null)
        => new(cookie, handler: new FakeHttpHandler(respond));

    [Fact]
    public async Task GetCsrfTokenAsync_ReadsTokenFromFailureHeader()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("x-csrf-token", "csrf-abc");
        var client = Client(_ => response);

        var (token, success, _) = await client.GetCsrfTokenAsync();

        Assert.True(success);
        Assert.Equal("csrf-abc", token);
    }

    [Fact]
    public async Task GetAuthTicketAsync_SendsCsrfHeader_ThenReadsTicket()
    {
        var client = Client(request =>
        {
            // First call carries no CSRF header → 403 with token; second sends it → ticket.
            if (!request.Headers.Contains("X-CSRF-TOKEN"))
            {
                var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
                forbidden.Headers.Add("x-csrf-token", "csrf-abc");
                return forbidden;
            }
            var ok = new HttpResponseMessage(HttpStatusCode.OK);
            ok.Headers.Add("rbx-authentication-ticket", "ticket-xyz");
            return ok;
        });

        var ticket = await client.GetAuthTicketAsync();

        Assert.Equal("ticket-xyz", ticket);
    }

    [Fact]
    public async Task LoginAsync_HandlesCsrfChallenge_AndReadsCookie()
    {
        // First POST without a token → 403 + x-csrf-token; retry with it → cookie in Set-Cookie.
        string? retryBody = null;
        var client = Client(request =>
        {
            if (!request.Headers.Contains("X-CSRF-TOKEN"))
            {
                var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden);
                forbidden.Headers.Add("x-csrf-token", "csrf-login");
                return forbidden;
            }
            retryBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            var ok = new HttpResponseMessage(HttpStatusCode.OK);
            ok.Headers.Add("Set-Cookie", ".ROBLOSECURITY=cookie-abc; Domain=.roblox.com; Expires=Thu, 01 Jan 2026 00:00:00 GMT; Path=/; HttpOnly; Secure");
            ok.Content = new StringContent("{\"user\":{\"id\":42,\"name\":\"TestUser\"}}");
            return ok;
        });

        var (cookie, error) = await client.LoginAsync("TestUser", "hunter2");

        Assert.Null(error);
        Assert.Equal("cookie-abc", cookie);
        // The credential pair must ride in ctype/cvalue — "username" is rejected by Roblox
        // with 400 code 3 ("Username and Password are required", field ctype).
        Assert.NotNull(retryBody);
        Assert.Contains("\"cvalue\":\"TestUser\"", retryBody);
        Assert.DoesNotContain("\"username\"", retryBody);
    }

    [Fact]
    public async Task LoginAsync_ReturnsFriendlyError_ForBadCredentials()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"errors\":[{\"code\":400,\"message\":\"Incorrect username or password.\"}]}")
        });

        var (cookie, error) = await client.LoginAsync("TestUser", "wrong-password");

        Assert.Null(cookie);
        Assert.Equal("Incorrect username or password.", error);

        // The dev-facing copy button needs the raw body + status, not just the friendly text.
        Assert.NotNull(client.LastLoginErrorBody);
        Assert.StartsWith("[401]", client.LastLoginErrorBody);
        Assert.Contains("Incorrect username", client.LastLoginErrorBody);
    }

    [Fact]
    public async Task LoginAsync_SurfacesCaptchaRequirement()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"errors\":[{\"code\":1,\"message\":\"CaptchaRequired\",\"userFacingMessage\":\"Roblox is asking for you to verify you are a human.\"}]}")
        });

        var (cookie, error) = await client.LoginAsync("TestUser", "hunter2");

        Assert.Null(cookie);
        Assert.Contains("CAPTCHA", error);
    }

    [Fact]
    public async Task LoginAsync_SurfacesChallengeRequirement()
    {
        // Roblox's anti-bot gate (403 code 0) — must map to actionable guidance, not a raw dump.
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"errors\":[{\"code\":0,\"message\":\"Challenge is required to authorize the request\"}]}")
        });

        var (cookie, error) = await client.LoginAsync("TestUser", "hunter2");

        Assert.Null(cookie);
        Assert.Contains("CAPTCHA", error);
        Assert.Contains("built-in browser", error);
    }

    [Fact]
    public async Task GetAccountInfoAsync_ParsesUsernameAndUserId()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"UserID\":42,\"UserName\":\"TestUser\",\"DisplayName\":\"Test User\"}")
        });

        var (username, userId, error) = await client.GetAccountInfoAsync();

        Assert.Null(error);
        Assert.Equal("TestUser", username);
        Assert.Equal(42, userId);
    }

    [Fact]
    public async Task GetAccountInfoAsync_ReportsInvalidCookie()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var (username, userId, error) = await client.GetAccountInfoAsync();

        Assert.Null(username);
        Assert.Null(userId);
        Assert.StartsWith("[401]", error);
    }

    [Fact]
    public async Task GetCsrfTokenAsync_Error_DoesNotEchoRawBody()
    {
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(new string('X', 500))
        });

        var (token, success, error) = await client.GetCsrfTokenAsync();

        Assert.False(success);
        Assert.Null(token);
        Assert.StartsWith("[403]", error);
        Assert.True(error!.Length <= 250, "error body must be truncated");
    }
}

public class RobloxCookieTests
{
    [Theory]
    [InlineData("_|WARNING:-DO-NOT-SHARE-abc123", "_|WARNING:-DO-NOT-SHARE-abc123")]
    [InlineData(" .ROBLOSECURITY=abc123 ", "abc123")]
    [InlineData("Cookie: .ROBLOSECURITY=abc123; Domain=.roblox.com; Path=/", "abc123")]
    [InlineData("Cookie:.ROBLOSECURITY=abc123; HttpOnly; Secure", "abc123")]
    [InlineData("\"abc123\"", "abc123")]
    [InlineData("abc123; HttpOnly; Secure", "abc123")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void Clean_NormalizesPastedCookies(string raw, string? expected)
    {
        Assert.Equal(expected, RobloxCookie.Clean(raw));
    }
}
