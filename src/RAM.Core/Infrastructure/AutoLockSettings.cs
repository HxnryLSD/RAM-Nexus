namespace RAM.Core.Infrastructure;

/// <summary>
/// Typed access to the vault auto-lock settings persisted in RAMSettings.ini —
/// Bitwarden-style: re-lock the password-protected account file after a timeout
/// while the machine is idle, and/or immediately when the window is minimized.
/// Pure logic (no window, no timers) so the lock decisions are unit-testable.
/// </summary>
public class AutoLockSettings
{
    public const string EnabledKey = "AutoLockEnabled";
    public const string TimeoutMinutesKey = "AutoLockTimeoutMinutes";
    public const string LockOnMinimizeKey = "AutoLockOnMinimize";
    public const string LockOnIdleKey = "AutoLockOnIdle";

    /// <summary>Default timeout before an idle lock (Bitwarden's default is 15 min; 10 is close).</summary>
    public const int DefaultTimeoutMinutes = 10;

    /// <summary>Hard floor/ceiling for the timeout, minutes.</summary>
    public const int MinTimeoutMinutes = 1;
    public const int MaxTimeoutMinutes = 1440; // 24 h

    private readonly SettingsStore _settings;

    public AutoLockSettings(SettingsStore settings)
    {
        _settings = settings;
    }

    /// <summary>Master switch — when off, neither trigger ever locks.</summary>
    public bool Enabled
    {
        get => _settings.Get(EnabledKey, false);
        set { _settings.Set(EnabledKey, value ? "true" : "false"); _settings.Save(); }
    }

    /// <summary>Idle timeout in minutes, clamped to [1, 1440].</summary>
    public int TimeoutMinutes
    {
        get => Math.Clamp(_settings.Get(TimeoutMinutesKey, DefaultTimeoutMinutes), MinTimeoutMinutes, MaxTimeoutMinutes);
        set
        {
            _settings.Set(TimeoutMinutesKey, Math.Clamp(value, MinTimeoutMinutes, MaxTimeoutMinutes).ToString());
            _settings.Save();
        }
    }

    /// <summary>Lock immediately when the window is minimized.</summary>
    public bool LockOnMinimize
    {
        get => _settings.Get(LockOnMinimizeKey, true);
        set { _settings.Set(LockOnMinimizeKey, value ? "true" : "false"); _settings.Save(); }
    }

    /// <summary>Lock after <see cref="TimeoutMinutes"/> of system-wide inactivity.</summary>
    public bool LockOnIdle
    {
        get => _settings.Get(LockOnIdleKey, true);
        set { _settings.Set(LockOnIdleKey, value ? "true" : "false"); _settings.Save(); }
    }

    /// <summary>
    /// Whether the idle trigger should fire given how long the machine has been idle
    /// (seconds, from GetLastInputInfo). Also false when the master switch is off, the
    /// idle trigger is off, or the timeout is clamped to zero.
    /// </summary>
    public bool ShouldLockOnIdle(int idleSeconds)
    {
        if (!Enabled || !LockOnIdle) return false;
        int timeoutSeconds = TimeoutMinutes * 60;
        return idleSeconds >= timeoutSeconds;
    }
}
