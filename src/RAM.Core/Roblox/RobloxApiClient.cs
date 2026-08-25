using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RAM.Core.Roblox;

/// <summary>
/// Minimal, decoupled client for the Roblox Web API. Replaces the upstream RestSharp +
/// static AccountManager client setup with a single HttpClient + CookieContainer.
/// </summary>
public sealed class RobloxApiClient : IDisposable
{
    private const string BaseUrl = "https://www.roblox.com/";
    private const string AuthBaseUrl = "https://auth.roblox.com/";
    private const string Referer = "https://www.roblox.com/";

    private readonly HttpMessageHandler _handler;
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies;

    /// <summary>
    /// <paramref name="handler"/> exists for tests — pass a fake handler to exercise the
    /// request/response logic without the network. When omitted, the real handler is created
    /// with a cookie container so <see cref="SetCookie"/> works against live Roblox APIs.
    /// </summary>
    public RobloxApiClient(string? cookie = null, HttpMessageHandler? handler = null)
    {
        _cookies = new CookieContainer();
        _handler = handler ?? new HttpClientHandler { CookieContainer = _cookies, UseCookies = true };
        _http = new HttpClient(_handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RobloxAccountManager");
        if (cookie is not null) SetCookie(cookie);
    }

    public void SetCookie(string cookie)
    {
        _cookies.Add(new Uri(BaseUrl), new Cookie(".ROBLOSECURITY", cookie, "/", ".roblox.com"));
    }

    /// <summary>
    /// Raw body of the last failed login attempt, prefixed with the HTTP status
    /// (e.g. "[400] {…}"). Dev-facing only — never shown in the UI, which must not echo
    /// raw server bodies; the copy-error button in AddAccountDialog reads this.
    /// Null until a login fails with a non-OK response.
    /// </summary>
    public string? LastLoginErrorBody { get; private set; }

    /// <summary>
    /// Error responses must never echo raw server bodies back to UI surfaces: they can carry
    /// control characters, oversized payloads, or reflected tokens. Strip control characters
    /// and cap the length.
    /// </summary>
    private static string SanitizeBody(string body, int maxLength = 200)
    {
        if (string.IsNullOrEmpty(body)) return "";

        var sb = new StringBuilder(body.Length);
        foreach (char c in body)
            sb.Append(char.IsControl(c) ? ' ' : c);

        string cleaned = sb.ToString();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength] + "…";
    }

    private HttpRequestMessage Make(HttpMethod method, string relative)
    {
        var req = new HttpRequestMessage(method, relative);
        req.Headers.Referrer = new Uri(Referer);
        return req;
    }

    /// <summary>
    /// Obtain an X-CSRF-Token. The endpoint intentionally returns 403 + the header;
    /// we read the header from the failure response (upstream behavior).
    /// </summary>
    public async Task<(string? Token, bool Success, string? Error)> GetCsrfTokenAsync()
    {
        using var req = Make(HttpMethod.Post, "v1/authentication-ticket/");
        using var resp = await _http.SendAsync(req);
        string? token = resp.Headers.TryGetValues("x-csrf-token", out var v) ? v.FirstOrDefault() : null;
        if (string.IsNullOrEmpty(token))
            return (null, false, $"[{(int)resp.StatusCode}] {SanitizeBody(await resp.Content.ReadAsStringAsync())}");
        return (token, true, null);
    }

    /// <summary>Get an rbx-authentication-ticket for the authenticated account.</summary>
    public async Task<string?> GetAuthTicketAsync()
    {
        var (token, ok, _) = await GetCsrfTokenAsync();
        if (!ok) return null;

        using var req = Make(HttpMethod.Post, "v1/authentication-ticket/");
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Referrer = new Uri("https://www.roblox.com/games/4924922222/Brookhaven-RP");

        using var resp = await _http.SendAsync(req);
        return resp.Headers.TryGetValues("rbx-authentication-ticket", out var v) ? v.FirstOrDefault() : null;
    }

    /// <summary>
    /// Sign in with a username + password and return the .ROBLOSECURITY cookie, or a
    /// friendly error. Handles the X-CSRF challenge itself (same pattern as the ticket
    /// endpoint) and maps captcha / two-step verification / bad credentials to actionable
    /// messages instead of echoing raw server bodies.
    /// </summary>
    public async Task<(string? Cookie, string? Error)> LoginAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            return (null, "Enter both a username and a password.");

        // Contract per auth.roblox.com/v2/login: ctype + cvalue hold the credential pair;
        // a payload using "username" instead of "cvalue" is rejected with 400 code 3
        // ("Username and Password are required", field ctype).
        var body = new JObject
        {
            ["ctype"] = "Username",
            ["cvalue"] = username,
            ["password"] = password,
            ["captchaToken"] = null,
            ["captchaProvider"] = null,
            ["captchaId"] = null
        };
        string payload = body.ToString(Formatting.None);
        string url = AuthBaseUrl + "v2/login";

        // CSRF challenge: the first POST without a token returns 403 + x-csrf-token header.
        string? csrf = null;
        using (var first = await SendLoginAsync(url, payload, csrfToken: null))
        {
            csrf = first.Headers.TryGetValues("x-csrf-token", out var v) ? v.FirstOrDefault() : null;
            if (first.StatusCode == HttpStatusCode.OK && csrf is null)
                return (ReadRobloxSecurityCookie(first), null);
            if (csrf is null)
            {
                string raw = await first.Content.ReadAsStringAsync();
                LastLoginErrorBody = $"[{(int)first.StatusCode}] {raw}";
                return (null, DescribeLoginError(raw, (int)first.StatusCode));
            }
        }

        using var retry = await SendLoginAsync(url, payload, csrfToken: csrf);
        if (retry.StatusCode == HttpStatusCode.OK)
            return (ReadRobloxSecurityCookie(retry), null);
        string retryRaw = await retry.Content.ReadAsStringAsync();
        LastLoginErrorBody = $"[{(int)retry.StatusCode}] {retryRaw}";
        return (null, DescribeLoginError(retryRaw, (int)retry.StatusCode));
    }

    /// <summary>POST a login payload, optionally with the X-CSRF-TOKEN challenge token.</summary>
    private async Task<HttpResponseMessage> SendLoginAsync(string url, string payload, string? csrfToken)
    {
        using var req = Make(HttpMethod.Post, url);
        if (!string.IsNullOrEmpty(csrfToken))
            req.Headers.Add("X-CSRF-TOKEN", csrfToken);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return await _http.SendAsync(req);
    }

    /// <summary>Extract the .ROBLOSECURITY value from a login response's Set-Cookie header.</summary>
    private static string? ReadRobloxSecurityCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return null;
        foreach (var header in values)
        {
            string cookie = header.Split(';')[0].Trim();
            if (cookie.StartsWith(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase))
                return cookie[".ROBLOSECURITY=".Length..];
        }
        return null;
    }

    /// <summary>
    /// Map a failed login response to a friendly message. Known failure shapes (captcha,
    /// two-step verification, bad credentials, banned account) get tailored text; anything
    /// else falls back to a sanitized, length-capped body — never raw reflected input.
    /// </summary>
    private static string? DescribeLoginError(string body, int status)
    {
        string lower = body.ToLowerInvariant();
        if (lower.Contains("twostepverificationrequired") || lower.Contains("two step verification"))
            return "This account has two-step verification enabled — sign in on roblox.com first, then add the account by cookie.";
        // Roblox's anti-bot gate (CAPTCHA or the 403 code 0 "Challenge is required"). The API
        // can't solve these — the app falls through to its embedded browser, where the user
        // finishes the check by hand and the session cookie is captured automatically.
        if (lower.Contains("captcha") || lower.Contains("challenge is required"))
            return "Roblox is asking for a CAPTCHA (bot check) — the app opens its built-in browser so you can finish it by hand; your session is captured automatically.";
        if (lower.Contains("incorrect username") || lower.Contains("incorrect password") || lower.Contains("invalid credentials"))
            return "Incorrect username or password.";
        if (lower.Contains("\"isbanned\":true") || (lower.Contains("banned") && lower.Contains("isbanned")))
            return "This account is banned and cannot be signed in.";

        string sanitized = SanitizeBody(body);
        return string.IsNullOrEmpty(sanitized)
            ? $"Login failed (HTTP {status})."
            : $"Login failed: {sanitized}";
    }

    /// <summary>
    /// Fetch the authenticated account's username + user id using the cookie set on this
    /// client (the same my/account/json endpoint upstream RAM uses). Returns nulls with an
    /// error when the cookie is invalid or the response is unparseable.
    /// </summary>
    public async Task<(string? Username, long? UserId, string? Error)> GetAccountInfoAsync()
    {
        using var req = Make(HttpMethod.Get, "my/account/json");
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            return (null, null, $"[{(int)resp.StatusCode}] {SanitizeBody(await resp.Content.ReadAsStringAsync())}");

        try
        {
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return (json.Value<string>("UserName"), json.Value<long?>("UserID"), null);
        }
        catch (JsonException)
        {
            return (null, null, "Roblox returned an unexpected response.");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}
