using RAM.Core.Infrastructure;

namespace RAM.Core.Roblox.Rdd;

/// <summary>
/// The user's chosen "active" RDD install — the single deployment Accounts-page Launch,
/// the Fast Flags page and the FPS unlock all target. The setting stores the version
/// folder name, or the tag when the install is tagged (tags survive re-downloads to a
/// new version, folder names don't). All consumers resolve through
/// <see cref="Resolve"/> so launching and flag application always agree.
/// </summary>
public static class RddActiveInstall
{
    /// <summary>
    /// Resolve the active install folder, or null when nothing is installed. When the
    /// stored value no longer names an install (the folder was deleted, or superseded by
    /// a newer version of the same tag), the fallback — Default-tagged, then newest — is
    /// persisted in its place, so a deleted install can never resurrect itself as active
    /// via a later re-download of the same tag.
    /// </summary>
    public static string? Resolve(SettingsStore settings, RddDeploymentStore store)
    {
        string stored = settings.Get(RddOptions.ActiveInstallKey, "");
        string? folder = store.LocateActive(stored);

        // Only rewrite when there was an explicit choice that stopped resolving — a user
        // who never picked an active install keeps an unset key (resolution still works).
        if (folder is not null && !string.IsNullOrEmpty(stored) && !store.ActiveKeyResolves(stored))
            Set(settings, folder);

        return folder;
    }

    /// <summary>Persist an explicit choice of active install (a version folder).</summary>
    public static void Set(SettingsStore settings, string versionFolder)
    {
        string? root = Path.GetDirectoryName(versionFolder);
        var store = new RddDeploymentStore(root ?? ".");
        string value = store.GetTag(versionFolder) ?? new DirectoryInfo(versionFolder).Name;
        settings.Set(RddOptions.ActiveInstallKey, value);
        settings.Save();
    }
}
