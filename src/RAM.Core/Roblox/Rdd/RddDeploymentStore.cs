namespace RAM.Core.Roblox.Rdd;

/// <summary>
/// Manages RDD-installed Roblox deployments under a root folder. Each install is a
/// version-* folder optionally carrying a .ram-tag naming the exploit (or "Default").
/// </summary>
public sealed class RddDeploymentStore
{
    public const string TagFileName = RobloxDeploymentService.TagFileName;

    public string Root { get; }

    public RddDeploymentStore(string root)
    {
        Root = root;
    }

    /// <summary>All installed version folders (newest first).</summary>
    public IEnumerable<string> ListInstalls()
    {
        if (!Directory.Exists(Root)) yield break;

        foreach (var dir in Directory.EnumerateDirectories(Root, "version-*")
                     .OrderByDescending(Directory.GetLastWriteTime))
            yield return dir;
    }

    /// <summary>Read the tag of an install (exploit name or "Default"), or null if untagged.</summary>
    public string? GetTag(string versionFolder)
    {
        var tagFile = Path.Combine(versionFolder, TagFileName);
        return File.Exists(tagFile) ? File.ReadAllText(tagFile).Trim() : null;
    }

    /// <summary>Find the install tagged with the given exploit name (or "Default").</summary>
    public string? LocateTagged(string tag)
    {
        foreach (var install in ListInstalls())
            if (string.Equals(GetTag(install), tag, StringComparison.OrdinalIgnoreCase))
                return install;
        return null;
    }

    /// <summary>Latest installed deployment (fallback when no specific tag is requested).</summary>
    public string? LocateVersionFolder() => ListInstalls().FirstOrDefault();

    /// <summary>
    /// Resolve the active install. <paramref name="active"/> is the stored active value: a
    /// version folder name, or the tag when the install is tagged. Resolution order:
    /// exact folder-name match, then tag match (a re-download to a new version changes the
    /// folder name but keeps the tag), then the Default-tagged install, then the newest
    /// install. Null when nothing is installed.
    /// </summary>
    public string? LocateActive(string? active)
    {
        string? byKey = string.IsNullOrEmpty(active) ? null : LocateByKey(active);
        return byKey ?? LocateTagged("Default") ?? LocateVersionFolder();
    }

    /// <summary>Whether the stored active value still names an installed deployment (by folder name or tag).</summary>
    public bool ActiveKeyResolves(string? active)
        => !string.IsNullOrEmpty(active) && LocateByKey(active) is not null;

    private string? LocateByKey(string key)
    {
        foreach (var install in ListInstalls())
        {
            if (string.Equals(new DirectoryInfo(install).Name, key, StringComparison.OrdinalIgnoreCase))
                return install;
            if (string.Equals(GetTag(install), key, StringComparison.OrdinalIgnoreCase))
                return install;
        }
        return null;
    }
}
