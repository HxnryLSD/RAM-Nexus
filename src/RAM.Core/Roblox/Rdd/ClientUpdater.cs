using RAM.Core.Infrastructure;
using RAM.Core.Roblox.FastFlags;

namespace RAM.Core.Roblox.Rdd;

/// <summary>Freshness of the Default (latest live build) install relative to the live build.</summary>
public sealed record ClientUpdateInfo(string? CurrentVersion, string LiveVersion)
{
    /// <summary>True when a newer live build than the installed Default exists.</summary>
    public bool Available => CurrentVersion is not null &&
        !string.Equals(CurrentVersion, LiveVersion, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Outcome of a Default client update.</summary>
public sealed record ClientUpdateResult(InstallResult Install, bool SettingsApplied);

/// <summary>
/// Keeps the Default (latest live build) RDD install current: resolves the live Roblox
/// build, compares it with the installed Default, downloads the newer build under the
/// Default tag (the old folder is superseded only once the new one is fully in place),
/// and re-applies the user's fast flags + FPS unlock to the fresh install. Pure domain
/// logic — the shell drives it on a timer and surfaces the status.
/// </summary>
public sealed class ClientUpdater
{
    private readonly RobloxDeploymentService _service;
    private readonly InstallManager _manager;

    public ClientUpdater(RobloxDeploymentService? service = null, InstallManager? manager = null)
    {
        _service = service ?? new RobloxDeploymentService();
        _manager = manager ?? new InstallManager(_service);
    }

    /// <summary>
    /// Compare the live build with the installed Default. Returns null when no Default
    /// install exists yet — the RDD page bootstraps it, and this service never downloads
    /// a deployment the user didn't ask for.
    /// </summary>
    public async Task<ClientUpdateInfo?> CheckAsync(string root, CancellationToken ct = default)
    {
        var store = new RddDeploymentStore(root);
        string? defaultFolder = store.LocateTagged("Default");
        if (defaultFolder is null) return null;

        string live = await _service.ResolveCurrentVersionAsync(ct);
        return new ClientUpdateInfo(new DirectoryInfo(defaultFolder).Name, live);
    }

    /// <summary>
    /// Install the newest live build under the Default tag (a no-op/skip when the installed
    /// Default is already current) and re-apply the user's client settings to the resulting
    /// Default install — so an update never silently resets fast flags or the FPS unlock.
    /// </summary>
    public async Task<ClientUpdateResult> UpdateAsync(
        string root,
        SettingsStore settings,
        FastFlagStore fastFlags,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        bool parallelDownloads = true)
    {
        var result = await _manager.InstallAsync(root, "Default", exploit: null, force: false, progress, ct, parallelDownloads);

        bool applied = result.VersionFolder is not null &&
                       ClientSettingsApplier.Apply(result.VersionFolder, settings, fastFlags);
        return new ClientUpdateResult(result, applied);
    }
}
