using System.IO;

namespace RAM.Core;

/// <summary>
/// Single data root under the current user's LocalAppData folder. The vault, settings,
/// fast flags, crash log, RDD installs and update downloads all live under
/// <c>%LOCALAPPDATA%\Roblox Account Manager</c> instead of next to the executable, so the
/// data survives app reinstalls, folder moves and per-machine installs.
/// </summary>
public static class AppPaths
{
    /// <summary>Root of all app data: %LOCALAPPDATA%\Roblox Account Manager.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Roblox Account Manager");

    public static string AccountData => Path.Combine(Root, "AccountData.json");
    public static string Settings => Path.Combine(Root, "RAMSettings.ini");
    public static string FastFlags => Path.Combine(Root, "FastFlags.json");
    public static string CrashLog => Path.Combine(Root, "crash.log");
    public static string Rdd => Path.Combine(Root, "RDD");
    public static string Updates => Path.Combine(Root, "updates");

    /// <summary>
    /// One-time move of data files from the old exe-folder anchor into the AppData root.
    /// Runs before any store is constructed; never overwrites an existing file and never
    /// throws — a failed move must not break startup.
    /// </summary>
    public static void MigrateLegacyFiles(string? root = null, string? legacy = null)
    {
        root ??= Root;
        legacy ??= AppContext.BaseDirectory;
        try { Directory.CreateDirectory(root); } catch { return; }

        foreach (string name in new[] { "AccountData.json", "AccountData.json.backup", "AccountData.json.bak", "RAMSettings.ini", "FastFlags.json", "crash.log", "crash.log.old" })
            MoveIfAbsent(Path.Combine(legacy, name), Path.Combine(root, name));
        MoveDirIfAbsent(Path.Combine(legacy, "updates"), Path.Combine(root, "updates"));
        MoveDirIfAbsent(Path.Combine(legacy, "RDD"), Path.Combine(root, "RDD"));
    }

    private static void MoveIfAbsent(string from, string to)
    {
        try { if (File.Exists(from) && !File.Exists(to)) File.Move(from, to); } catch { /* best effort */ }
    }

    private static void MoveDirIfAbsent(string from, string to)
    {
        try { if (Directory.Exists(from) && !Directory.Exists(to)) Directory.Move(from, to); } catch { /* best effort */ }
    }
}
