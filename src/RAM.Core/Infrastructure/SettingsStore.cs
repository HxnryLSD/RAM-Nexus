using System.IO;

namespace RAM.Core.Infrastructure;

/// <summary>
/// Typed wrapper around an INI file used for application settings (RAMSettings.ini).
/// Mirrors the upstream AccountManager.IniSettings / General section accessors.
/// </summary>
public class SettingsStore
{
    private readonly IniFile _ini;
    private readonly string _path;

    /// <summary>The "General" section — the app's primary settings section.</summary>
    public IniSection General { get; }

    /// <summary>
    /// Default path is the user's LocalAppData data root (AppPaths.Settings) so settings
    /// are the same no matter how the app is launched or where it's installed. Tests pass
    /// explicit paths.
    /// </summary>
    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.Settings;
        _ini = File.Exists(_path) ? new IniFile(_path) : new IniFile();
        General = _ini.Section("General");
    }

    public void Save() => _ini.Save(_path);

    public T Get<T>(string key, T fallback = default!)
    {
        return General.Exists(key) ? General.Get<T>(key) : fallback;
    }

    public void Set(string key, string value)
    {
        General.Set(key, value);
    }
}
