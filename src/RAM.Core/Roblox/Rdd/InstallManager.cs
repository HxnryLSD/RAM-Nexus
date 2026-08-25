namespace RAM.Core.Roblox.Rdd;

/// <summary>
/// Orchestrates RDD installs: resolves the version (pinned by an exploit or the live build),
/// skips installs that are already present, and supersedes older folders carrying the same
/// tag. Failure and cancellation safety lives in the deployment service, which stages every
/// install and only swaps it into place once complete — so a failed install can never damage
/// the previous copy and there is no partial folder to clean up here. Pure domain logic — the
/// UI only reports progress and reacts to the result.
/// </summary>
public sealed class InstallManager
{
    private readonly RobloxDeploymentService _service;

    public InstallManager(RobloxDeploymentService? service = null)
    {
        _service = service ?? new RobloxDeploymentService();
    }

    /// <summary>
    /// Install a deployment under <paramref name="root"/>. <paramref name="tag"/> names the
    /// install (exploit title or "Default"); <paramref name="exploit"/> pins the version
    /// (null → resolve the latest live build). With <paramref name="force"/>, replaces an
    /// existing tagged install instead of skipping it.
    /// </summary>
    public async Task<InstallResult> InstallAsync(
        string root,
        string? tag,
        Exploit? exploit,
        bool force,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default,
        bool parallelDownloads = true)
    {
        tag ??= "Default";
        var store = new RddDeploymentStore(root);

        try
        {
            string version = exploit?.RbxVersion ?? "";
            progress?.Report(new InstallProgress(InstallPhase.Resolving, "", 0, 0,
                exploit is null
                    ? "Resolving the latest WindowsPlayer version…"
                    : $"Downloading {tag} (Roblox {version})…"));

            if (string.IsNullOrEmpty(version))
                version = await _service.ResolveCurrentVersionAsync(ct);

            string? tagged = store.LocateTagged(tag);

            // Same tag + same version already installed → nothing to download.
            if (!force && tagged is not null &&
                string.Equals(new DirectoryInfo(tagged).Name, version, StringComparison.OrdinalIgnoreCase))
                return new InstallResult(InstallResultKind.Skipped, tagged, $"{tag} is already installed ({version}).");

            try
            {
                // The service stages the whole deployment under root and atomically renames it
                // into place only once every file is verified — so a cancelled or failed install
                // can't touch the previous copy, and force swaps the old same-version folder
                // out instead of deleting it first.
                string folder = await _service.InstallAsync(version, root, tag, progress, ct, parallelDownloads);

                // One install per tag: a newer version under the same tag supersedes the old
                // tagged folder. Only delete it now that the replacement is fully in place, so
                // a failed install never leaves the user without their previous copy.
                if (tagged is not null && !string.Equals(tagged, folder, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(tagged, true); } catch { /* best effort */ }
                }

                return new InstallResult(InstallResultKind.Installed, folder, $"Installed {tag} → {folder}");
            }
            catch (OperationCanceledException)
            {
                return new InstallResult(InstallResultKind.Cancelled, null, "Download cancelled.");
            }
            catch (Exception ex)
            {
                return new InstallResult(InstallResultKind.Failed, null, ex.Message);
            }
        }
        catch (OperationCanceledException)
        {
            return new InstallResult(InstallResultKind.Cancelled, null, "Download cancelled.");
        }
        catch (Exception ex)
        {
            return new InstallResult(InstallResultKind.Failed, null, ex.Message);
        }
    }
}
