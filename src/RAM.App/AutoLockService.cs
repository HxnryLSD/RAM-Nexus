using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using RAM.Core.Infrastructure;

namespace RAM;

/// <summary>
/// Bitwarden-style vault auto-lock. Re-locks the password-protected account file
/// immediately when the window is minimized (if enabled) and after the configured
/// timeout of system-wide inactivity (if enabled). When the window is restored and
/// the vault turned out to be locked, the unlock prompt is shown again.
///
/// No-ops unless the account file is password-locked — DPAPI / plaintext vaults
/// have nothing to lock, so the service stays out of the way. Dependencies are
/// injected; the lock/unlock actions come from AppServices.
/// </summary>
public sealed class AutoLockService
{
    /// <summary>How often to re-check idle time while enabled.</summary>
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(15);

    private readonly AutoLockSettings _settings;
    private readonly AccountStore _store;
    private readonly Action _lockVault;
    private readonly Func<Task<bool>> _unlockAccounts;
    private readonly DispatcherTimer _timer;
    private bool _wasMinimized;

    public AutoLockService(
        MainWindow window,
        SettingsStore settings,
        AccountStore store,
        Action lockVault,
        Func<Task<bool>> unlockAccounts)
    {
        _settings = new AutoLockSettings(settings);
        _store = store;
        _lockVault = lockVault;
        _unlockAccounts = unlockAccounts;

        window.AppWindow.Changed += OnAppWindowChanged;

        _timer = new DispatcherTimer { Interval = IdleCheckInterval };
        _timer.Tick += (_, _) => CheckIdle();
        _timer.Start();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPresenterChange || sender.Presenter is not OverlappedPresenter presenter)
            return;

        bool minimized = presenter.State == OverlappedPresenterState.Minimized;

        // Minimize → lock immediately (Bitwarden's "lock on minimize").
        if (minimized && _settings.Enabled && _settings.LockOnMinimize)
            _lockVault();

        // Restore → if the vault is locked (auto-lock or manual), ask to unlock again.
        if (!minimized && _wasMinimized && _settings.Enabled && IsVaultLocked())
            _ = _unlockAccounts();

        _wasMinimized = minimized;
    }

    private void CheckIdle()
    {
        if (_settings.ShouldLockOnIdle(GetIdleSeconds()))
            _lockVault();
    }

    /// <summary>Whether the vault is password-locked and not unlocked this session.</summary>
    private bool IsVaultLocked()
        => _store.DetectMode() == AccountStoreMode.PasswordLocked && _store.SessionPassword is null;

    /// <summary>System-wide idle time (seconds) since the last keyboard/mouse input anywhere.</summary>
    private static int GetIdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return 0;

        // Unsigned subtraction survives the 32-bit TickCount wrap (~24.9 days uptime).
        uint idleMs = (uint)(Environment.TickCount - (int)info.dwTime);
        return (int)(idleMs / 1000);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
