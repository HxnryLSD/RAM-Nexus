namespace RAM.Core.Roblox;

/// <summary>
/// Helpers for the .ROBLOSECURITY cookie values users paste into the app. Pasted cookies
/// arrive in many shapes (bare token, full "_|WARNING:…" value, a "Cookie:" header line,
/// with or without attributes) — <see cref="Clean"/> normalizes them all to the raw value
/// that the API calls and the store expect.
/// </summary>
public static class RobloxCookie
{
    /// <summary>
    /// Normalize a pasted cookie value: trim whitespace/quotes, strip a "Cookie:" header
    /// prefix and the ".ROBLOSECURITY=" name, and drop any "; attribute" tail. Returns
    /// null when nothing usable is left.
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string value = raw.Trim().Trim('"', '\'');

        // "Cookie: .ROBLOSECURITY=…" — a full header line pasted by mistake.
        int colon = value.IndexOf(':');
        if (colon > 0 && value[..colon].Trim().Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            value = value[(colon + 1)..].Trim();

        // ".ROBLOSECURITY=…" — name included.
        if (value.StartsWith(".ROBLOSECURITY=", StringComparison.OrdinalIgnoreCase))
            value = value[".ROBLOSECURITY=".Length..];

        // "…; Domain=…; Path=/; HttpOnly" — trailing attributes.
        int semi = value.IndexOf(';');
        if (semi >= 0) value = value[..semi];

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }
}
