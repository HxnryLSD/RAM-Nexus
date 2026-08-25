using System.Collections.Concurrent;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace RAM.Core.Roblox.Rdd;

/// <summary>
/// Client for the WEAO RDD exploit/version API (the source behind the RDD page's
/// exploit pills). The API is Cloudflare-protected, so callers should treat failures
/// as "exploit data unavailable" and fall back to a Default install.
/// </summary>
public sealed class WeaoRddApiClient
{
    // Mirrors index.js: TYPES = which extypes are offered at all, PLAT_ORDER = the
    // executor-class order used to sort the exploit pills.
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
        { "wexecutor", "wexternal", "mexecutor" };
    private static readonly Dictionary<string, int> PlatOrder = new(StringComparer.OrdinalIgnoreCase)
        { ["wexecutor"] = 0, ["wexternal"] = 1, ["mexecutor"] = 2 };

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Default freshness window for cached responses.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    // Last-known-good responses keyed by request URL, shared across clients so the RDD page
    // stays populated across page visits and while offline (stale-while-revalidate). A few
    // KB of JSON per endpoint; bounded by the handful of URLs the client knows.
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new();

    private readonly HttpClient _http;
    private readonly bool _useCache;
    private readonly TimeSpan _cacheTtl;
    public string BaseUrl { get; }

    public WeaoRddApiClient(string baseUrl = "https://weao.gg", HttpClient? http = null,
        bool useCache = true, TimeSpan? cacheTtl = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        // Shared client so repeated page visits don't churn sockets.
        _http = http ?? SharedHttp;
        _useCache = useCache;
        _cacheTtl = cacheTtl ?? DefaultCacheTtl;
    }

    private sealed record CacheEntry(string Json, DateTimeOffset StoredAt);

    /// <summary>
    /// Fetch the exploit list from /api/status/exploits, filtered and sorted exactly like the
    /// reference RDD page: hidden exploits dropped, only the known extypes kept (wexecutor /
    /// wexternal / mexecutor), ordered by executor class then index. The deployment version is
    /// read from "rbxversion" — the field the reference page uses for the download — never
    /// "version" (the exploit's own release number).
    /// </summary>
    public async Task<List<Exploit>> GetExploitsAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var json = await GetWithCacheAsync($"{BaseUrl}/api/status/exploits", forceRefresh, ct);
        var arr = JArray.Parse(json);
        var result = new List<Exploit>(arr.Count);

        foreach (var token in arr)
        {
            result.Add(new Exploit
            {
                Title = token.Value<string>("title") ?? "",
                RbxVersion = token.Value<string>("rbxversion"),
                Extype = token.Value<string>("extype") ?? "",
                Platform = token.Value<string>("platform") ?? "",
                Hidden = token.Value<bool>("hidden"),
                Index = token.Value<int?>("index"),
                Cost = token.Value<string>("cost"),
                WebsiteLink = token.Value<string>("websitelink"),
                DiscordLink = token.Value<string>("discordlink")
            });
        }

        return result
            .Where(e => !e.Hidden && Types.Contains(e.Extype))
            .OrderBy(e => PlatOrder.TryGetValue(e.Extype, out var order) ? order : 9)
            .ThenBy(e => e.Index ?? 99)
            .ToList();
    }

    /// <summary>Fetch current + previous versions from /api/versions/current and /past.</summary>
    public async Task<RddVersionSnapshot> GetVersionSnapshotAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var current = await GetVersionsAsync("current", forceRefresh, ct);
        var past = await GetVersionsAsync("past", forceRefresh, ct);

        return new RddVersionSnapshot
        {
            WindowsCurrent = current?.Value<string>("Windows"),
            WindowsPrevious = past?.Value<string>("Windows")
        };
    }

    private async Task<JObject?> GetVersionsAsync(string which, bool forceRefresh, CancellationToken ct)
    {
        try
        {
            var json = await GetWithCacheAsync($"{BaseUrl}/api/versions/{which}", forceRefresh, ct);
            return JObject.Parse(json);
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation must stay cancellation, not be conflated with "no data"
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fetch a URL with cache semantics: serve fresh entries directly; serve stale entries
    /// immediately while revalidating in the background (stale-while-revalidate); fall back to
    /// the last-known-good copy when a fetch fails, so an offline page load still shows the
    /// exploit list it had before. <paramref name="forceRefresh"/> (the page's explicit
    /// Refresh button) bypasses the stale branch and hits the network, still falling back to
    /// the cache on failure.
    /// </summary>
    private async Task<string> GetWithCacheAsync(string url, bool forceRefresh, CancellationToken ct)
    {
        if (_useCache && Cache.TryGetValue(url, out var entry))
        {
            bool fresh = DateTimeOffset.UtcNow - entry.StoredAt < _cacheTtl;
            if (fresh && !forceRefresh)
                return entry.Json;
            if (!forceRefresh)
            {
                _ = RefreshAsync(url); // serve stale now, refresh in the background
                return entry.Json;
            }

            try
            {
                return await FetchAndCacheAsync(url, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return entry.Json; // explicit refresh failed — keep showing the last good list
            }
        }

        return await FetchAndCacheAsync(url, ct);
    }

    private async Task<string> FetchAndCacheAsync(string url, CancellationToken ct)
    {
        string json = await _http.GetStringAsync(url, ct);
        if (_useCache)
            Cache[url] = new CacheEntry(json, DateTimeOffset.UtcNow);
        return json;
    }

    /// <summary>Background revalidation; failures keep the stale entry. Never observed — errors are swallowed here.</summary>
    private async Task RefreshAsync(string url)
    {
        try
        {
            string json = await _http.GetStringAsync(url, CancellationToken.None);
            Cache[url] = new CacheEntry(json, DateTimeOffset.UtcNow);
        }
        catch
        {
            // keep serving the stale copy
        }
    }
}
