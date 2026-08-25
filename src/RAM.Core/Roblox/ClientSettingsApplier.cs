using RAM.Core.Infrastructure;
using RAM.Core.Roblox.FastFlags;

namespace RAM.Core.Roblox;

/// <summary>
/// Applies the user's client settings (FPS unlock + activated fast flags) to an install,
/// reading them from the persisted stores. This is the single source of truth shared by
/// the Fast Flags page and the background Default-client updater, so a freshly updated
/// install always starts from exactly what the page shows.
/// </summary>
public static class ClientSettingsApplier
{
    public static bool Apply(string versionFolder, SettingsStore settings, FastFlagStore fastFlags)
    {
        bool unlockFps = settings.Get("UnlockFPS", false);
        int maxFps = settings.Get("MaxFPSValue", ClientSettingsPatcher.DefaultMaxFps);
        return ClientSettingsPatcher.PatchSettings(versionFolder, unlockFps, maxFps, fastFlags: fastFlags.GetActivated());
    }
}
