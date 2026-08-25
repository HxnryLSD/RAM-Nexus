using System.Net.Http;

namespace RAM.Core;

/// <summary>
/// The User-Agent every outgoing HTTP request must carry: GitHub's API hard-rejects
/// UA-less clients (HTTP 403 — which silently broke the app-update check), and WEAO /
/// CDN logs expect an identifiable product. The version segment tracks the assembly's
/// <c>&lt;Version&gt;</c> so release builds identify themselves.
/// </summary>
internal static class UserAgent
{
    public static readonly string Value =
        $"RobloxAccountManager/{typeof(UserAgent).Assembly.GetName().Version?.ToString(3) ?? "1.0"}";

    /// <summary>Make <paramref name="client"/> send the product UA (idempotent).</summary>
    public static void Apply(HttpClient client)
    {
        if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"{Value} (+https://github.com/HxnryLSD/RAM-Nexus)");
    }
}
