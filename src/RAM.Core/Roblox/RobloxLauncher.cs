using System.Diagnostics;
using System.Text;

namespace RAM.Core.Roblox;

/// <summary>
/// Launches a Roblox game through a resolved install. Callers pass the version folder in,
/// so the same launcher works against the default install and any RDD-tagged install.
/// </summary>
public sealed class RobloxLauncher
{
    /// <summary>The player executable of an install (launcher stub, falling back to the beta
    /// player — what RDD installs ship), or null when neither is present.</summary>
    public static string? ResolvePlayerExe(string versionFolder)
    {
        string exe = Path.Combine(versionFolder, "RobloxPlayerLauncher.exe");
        if (!File.Exists(exe))
            exe = Path.Combine(versionFolder, "RobloxPlayerBeta.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Start the Roblox player itself (no place) — just opens the player/home screen.</summary>
    public Process? LaunchPlayer(string versionFolder)
    {
        string? exe = ResolvePlayerExe(versionFolder);
        if (exe is null) return null;
        return Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>
    /// Start the Roblox player against a place. Returns the started process, or null if the
    /// install/launcher exe could not be resolved. Launch argument format may need adjustment
    /// per Roblox version (verified against a real install before wiring into the UI).
    /// </summary>
    public Process? LaunchPlace(string versionFolder, long placeId, string? jobId = null)
    {
        string? exe = ResolvePlayerExe(versionFolder);
        if (exe is null) return null;

        var args = $" --app {placeId}";
        if (!string.IsNullOrEmpty(jobId)) args += $" {jobId}";

        var psi = new ProcessStartInfo(exe, args.TrimStart()) { UseShellExecute = true };
        return Process.Start(psi);
    }

    // ---- Account-based launching ----

    /// <summary>
    /// The PlaceLauncher.ashx joinscript URL the player fetches when joining a place.
    /// Carries the place (and optional job) plus a browser tracker id Roblox uses to
    /// tie the launch to a session — same shape upstream RAM builds.
    /// </summary>
    public static string BuildJoinscriptUrl(long placeId, string? jobId = null, string? browserTrackerId = null)
    {
        string request = string.IsNullOrEmpty(jobId) ? "RequestGame" : "RequestGameJob";
        var url = new StringBuilder("https://assetgame.roblox.com/game/PlaceLauncher.ashx?")
            .Append("request=").Append(request)
            .Append("&placeId=").Append(placeId);
        if (!string.IsNullOrEmpty(browserTrackerId))
            url.Append("&browserTrackerId=").Append(Uri.EscapeDataString(browserTrackerId));
        if (!string.IsNullOrEmpty(jobId))
            url.Append("&gameId=").Append(Uri.EscapeDataString(jobId));
        url.Append("&isPlayTogetherGame=false");
        return url.ToString();
    }

    /// <summary>
    /// Command line that starts the player authenticated as the account the auth ticket
    /// belongs to (the ticket is fetched from auth.roblox.com using the account's
    /// .ROBLOSECURITY cookie). Same <c>--app -t -j</c> shape upstream RAM uses.
    /// </summary>
    public static string BuildAccountLaunchArgs(string authTicket, long placeId, string? jobId = null, string? browserTrackerId = null)
        => $"--app -t {authTicket} -j \"{BuildJoinscriptUrl(placeId, jobId, browserTrackerId)}\"";

    /// <summary>
    /// Start the player into <paramref name="placeId"/> as the account behind
    /// <paramref name="authTicket"/>. Returns the started process, or null when the
    /// install/launcher exe could not be resolved.
    /// </summary>
    public Process? LaunchPlaceAsAccount(string versionFolder, string authTicket, long placeId, string? jobId = null, string? browserTrackerId = null)
    {
        string? exe = ResolvePlayerExe(versionFolder);
        if (exe is null) return null;

        var psi = new ProcessStartInfo(exe, BuildAccountLaunchArgs(authTicket, placeId, jobId, browserTrackerId))
        {
            UseShellExecute = true
        };
        return Process.Start(psi);
    }
}
