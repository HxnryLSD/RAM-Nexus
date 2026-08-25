using Newtonsoft.Json.Linq;
using RAM.Core.Roblox.FastFlags;

namespace RAM.Core.Roblox;

/// <summary>
/// Writes ClientAppSettings.json into a resolved Roblox version folder.
/// Unlike the upstream patcher it does NOT locate the install itself — callers pass the
/// folder in (so it works against any RDD-tagged install or the default registry install).
/// Can merge FPS unlock and/or activated fast flags into the file.
/// </summary>
public static class ClientSettingsPatcher
{
    public const string SettingsFileName = "ClientAppSettings.json";
    public const int DefaultMaxFps = 240;

    /// <summary>
    /// Applies FPS-unlock, fast flags and/or a custom settings file to a resolved version folder.
    /// </summary>
    /// <param name="fastFlags">Activated fast flags (name -> string value) to merge in.</param>
    /// <returns>True if settings were written.</returns>
    public static bool PatchSettings(
        string versionFolder,
        bool unlockFps,
        int maxFps = DefaultMaxFps,
        string? customSettingsPath = null,
        IReadOnlyDictionary<string, string>? fastFlags = null)
    {
        if (string.IsNullOrEmpty(versionFolder) || !Directory.Exists(versionFolder)) return false;

        var name = new DirectoryInfo(versionFolder).Name;
        if (!name.StartsWith("version-")) return false;

        bool hasLauncher = File.Exists(Path.Combine(versionFolder, "RobloxPlayerLauncher.exe"))
                        || File.Exists(Path.Combine(versionFolder, "RobloxPlayerBeta.exe"));
        if (!hasLauncher) return false;

        var settingsFolder = Path.Combine(versionFolder, "ClientSettings");
        Directory.CreateDirectory(settingsFolder);
        var settingsFile = Path.Combine(settingsFolder, SettingsFileName);

        // A user-specified settings file takes precedence as the base; nothing else is merged.
        if (!string.IsNullOrEmpty(customSettingsPath) && File.Exists(customSettingsPath))
        {
            File.Copy(customSettingsPath, settingsFile, overwrite: true);
            return true;
        }

        // Nothing requested → still clean up any RAM-managed keys left in the file,
        // so a flag the user turned off is not left active in the install.
        bool hasFastFlags = fastFlags is { Count: > 0 };

        JObject settings;
        if (File.Exists(settingsFile) && Utilities.TryParseJson<JObject>(File.ReadAllText(settingsFile), out var loaded))
            settings = loaded;
        else
            settings = new JObject();

        // Drop every RAM-managed key (known fast-flag names + the FPS unlock) that is no
        // longer active — the file must reflect exactly what the user has on, never stale
        // leftovers. Roblox's own keys (not in the allow list) are preserved.
        var managedKeys = FastFlagCatalog.All.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        managedKeys.Add("DFIntTaskSchedulerTargetFps");
        foreach (var key in settings.Properties().Select(p => p.Name).ToList())
        {
            if (managedKeys.Contains(key))
                settings.Remove(key);
        }

        if (unlockFps)
            settings["DFIntTaskSchedulerTargetFps"] = Math.Clamp(maxFps, 30, 1000);

        if (hasFastFlags)
        {
            foreach (var (flag, value) in fastFlags!)
            {
                if (string.IsNullOrEmpty(flag) || value is null) continue;
                settings[flag] = ToJValue(value);
            }
        }

        // Nothing left to write → restore Roblox defaults by removing the file.
        if (settings.Count == 0)
        {
            if (File.Exists(settingsFile))
                File.Delete(settingsFile);
            return true;
        }

        File.WriteAllText(settingsFile, settings.ToString(Newtonsoft.Json.Formatting.None));
        return true;
    }

    /// <summary>Convert a stored string value into the correct JSON token (bool/int/string).</summary>
    public static JValue ToJValue(string value)
    {
        if (bool.TryParse(value, out var b)) return new JValue(b);
        if (long.TryParse(value, out var l)) return new JValue(l);
        return new JValue(value);
    }
}
