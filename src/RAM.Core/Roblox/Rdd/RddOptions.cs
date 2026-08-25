namespace RAM.Core.Roblox.Rdd;

/// <summary>
/// Shared settings keys and defaults for RDD installs, kept in Core so every consumer
/// (RDD page, Fast Flags page, launcher) agrees on where installs live.
/// </summary>
public static class RddOptions
{
    /// <summary>SettingsStore key for the folder RDD installs live in.</summary>
    public const string SettingsKey = "RDDInstallRoot";

    /// <summary>
    /// SettingsStore key for the active install: the version folder name, or the tag when
    /// the install is tagged (see <see cref="RddActiveInstall"/>). The active install is
    /// the deployment Accounts-page Launch and the Fast Flags page target.
    /// </summary>
    public const string ActiveInstallKey = "RDDActiveInstall";

    /// <summary>
    /// SettingsStore key for the self-maintaining Default install toggle (default on): the
    /// background service keeps the Default-tagged install on the latest live build and
    /// re-applies fast flags + FPS unlock after each update.
    /// </summary>
    public const string AutoUpdateKey = "ClientAutoUpdate";

    /// <summary>
    /// SettingsStore key for parallel manifest downloads (default on, mirroring the reference
    /// RDD page's "Parallel Downloads" checkbox). When off, deployment files download serially.
    /// </summary>
    public const string ParallelDownloadsKey = "RDDParallelDownloads";

    /// <summary>Default install root: the "RDD" folder under the user's AppData data root.</summary>
    public static string DefaultRoot => AppPaths.Rdd;
}
