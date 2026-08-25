using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RAM.Core.Infrastructure;
using RAM.Core.Roblox.Rdd;
using RAM.Dialogs;

namespace RAM.Views;

public sealed partial class SettingsPage : Page
{
    /// <summary>Timeout choices offered by the Auto-lock dropdown (minutes).</summary>
    private static readonly int[] TimeoutPresets = { 1, 5, 10, 15, 30, 60, 240 };

    private AutoLockSettings? _autoLock;
    private bool _loading = true;

    private AppServices? _services;

    public SettingsPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (AppServices)e.Parameter;

        _loading = true;
        LoadAppearance();
        UnlockFpsToggle.IsOn = _services.Settings.Get("UnlockFPS", false);
        MaxFpsBox.Value = _services.Settings.Get("MaxFPSValue", 240);
        AutoUpdateClientToggle.IsOn = _services.Settings.Get(RddOptions.AutoUpdateKey, true);
        UpdatePasswordSection();
        _autoLock = new AutoLockSettings(_services.Settings);
        LoadAutoLock();
        _loading = false;

        // Refresh the Security section if auto-lock fires while this page is open.
        _services.VaultLocked += UpdatePasswordSection;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _services!.VaultLocked -= UpdatePasswordSection;
    }

    // ---- Appearance (window material) ----

    private void LoadAppearance()
    {
        BackdropToggle.IsOn = _services!.Settings.Get("BackdropEnabled", true);
        BackdropTransparencyToggle.IsOn = _services.Settings.Get("BackdropTransparency", true);

        BackdropModeBox.SelectedIndex = _services.Settings.Get("BackdropMode", "Mica") switch
        {
            "MicaAlt" => 1,
            "Acrylic" => 2,
            "None" => 3,
            _ => 0
        };

        UpdateTransparencyAvailability();
    }

    /// <summary>Transparency only affects Acrylic — Mica is translucent by design.</summary>
    private void UpdateTransparencyAvailability()
        => BackdropTransparencyToggle.IsEnabled = CurrentMode() == "Acrylic";

    private string CurrentMode() => BackdropModeBox.SelectedIndex switch
    {
        1 => "MicaAlt",
        2 => "Acrylic",
        3 => "None",
        _ => "Mica"
    };

    private void BackdropToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading) return;
        _services!.Settings.Set("BackdropEnabled", BackdropToggle.IsOn ? "true" : "false");
        _services.Settings.Save();
        _services.RootWindow.ApplyBackdrop(_services.Settings);
    }

    private void BackdropModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BackdropModeBox.SelectedIndex < 0) return;
        _services!.Settings.Set("BackdropMode", CurrentMode());
        _services.Settings.Save();
        UpdateTransparencyAvailability();
        _services.RootWindow.ApplyBackdrop(_services.Settings);
    }

    private void BackdropTransparencyToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading) return;
        _services!.Settings.Set("BackdropTransparency", BackdropTransparencyToggle.IsOn ? "true" : "false");
        _services.Settings.Save();
        _services.RootWindow.ApplyBackdrop(_services.Settings);
    }

    // ---- Roblox ----

    private void UnlockFpsToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading) return;
        _services!.Settings.Set("UnlockFPS", UnlockFpsToggle.IsOn ? "true" : "false");
        _services.Settings.Save();
    }

    private void MaxFpsBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(MaxFpsBox.Value)) return;
        _services!.Settings.Set("MaxFPSValue", ((int)MaxFpsBox.Value).ToString());
        _services.Settings.Save();
    }

    private void AutoUpdateClientToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services!.Settings.Set(RddOptions.AutoUpdateKey, AutoUpdateClientToggle.IsOn ? "true" : "false");
        _services.Settings.Save();

        // Turning it back on re-checks right away instead of waiting for the hourly timer.
        if (AutoUpdateClientToggle.IsOn)
            _ = _services.ClientUpdater.RunOnceAsync();
    }

    // ---- Security (password lock) ----

    /// <summary>Refresh the encryption status text and the set/remove password buttons.</summary>
    private void UpdatePasswordSection()
    {
        var mode = _services!.Store.DetectMode();
        bool unlocked = _services.Store.SessionPassword is not null;

        EncryptionStatus.Text = mode switch
        {
            AccountStoreMode.PasswordLocked when unlocked =>
                "Accounts are encrypted with a password — unlocked this session.",
            AccountStoreMode.PasswordLocked =>
                "Accounts are encrypted with a password — locked. Unlock to view or change them.",
            AccountStoreMode.PlainText =>
                "⚠ Encryption is disabled (no-encryption marker file detected).",
            _ =>
                "Accounts are encrypted with this PC (DPAPI)."
        };

        SetPasswordButton.Content = mode == AccountStoreMode.PasswordLocked ? "Change password" : "Set password";
        bool canLock = mode == AccountStoreMode.PasswordLocked && unlocked;
        RemovePasswordButton.Visibility = canLock ? Visibility.Visible : Visibility.Collapsed;
        LockNowButton.Visibility = canLock ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Re-lock the account file without restarting: drop the session password and clear
    /// the in-memory accounts. The on-disk file stays password-encrypted — the store
    /// refuses any further save until the file is unlocked again.
    /// </summary>
    private void LockNow_Click(object sender, RoutedEventArgs e)
    {
        _services!.LockVault();
        UpdatePasswordSection();
    }

    // ---- Auto-lock ----

    private void LoadAutoLock()
    {
        AutoLockToggle.IsOn = _autoLock!.Enabled;
        AutoLockMinimizeToggle.IsOn = _autoLock.LockOnMinimize;
        AutoLockIdleToggle.IsOn = _autoLock.LockOnIdle;

        // Select the preset matching the saved timeout; a hand-edited INI value outside
        // the presets snaps to the nearest one.
        int minutes = _autoLock.TimeoutMinutes;
        int index = Array.IndexOf(TimeoutPresets, minutes);
        if (index < 0)
            index = TimeoutPresets.Select((m, i) => (m, i)).OrderBy(t => Math.Abs(t.m - minutes)).First().i;
        AutoLockTimeoutBox.SelectedIndex = index;

        UpdateAutoLockAvailability();
    }

    /// <summary>The timeout and trigger toggles only matter while auto-lock is on.</summary>
    private void UpdateAutoLockAvailability()
    {
        bool on = AutoLockToggle.IsOn;
        AutoLockTimeoutBox.IsEnabled = on;
        AutoLockMinimizeToggle.IsEnabled = on;
        AutoLockIdleToggle.IsEnabled = on;
    }

    private void AutoLockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _autoLock!.Enabled = AutoLockToggle.IsOn;
        UpdateAutoLockAvailability();
    }

    private void AutoLockTimeoutBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AutoLockTimeoutBox.SelectedIndex < 0) return;
        _autoLock!.TimeoutMinutes = TimeoutPresets[AutoLockTimeoutBox.SelectedIndex];
    }

    private void AutoLockMinimizeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _autoLock!.LockOnMinimize = AutoLockMinimizeToggle.IsOn;
    }

    private void AutoLockIdleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _autoLock!.LockOnIdle = AutoLockIdleToggle.IsOn;
    }

    private async void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        var mode = _services!.Store.DetectMode();

        // Locked but never unlocked this session: unlock first so accounts are in memory
        // (saving an empty list would wipe them) and the session password is set.
        if (mode == AccountStoreMode.PasswordLocked && _services.Store.SessionPassword is null)
        {
            if (!await _services.UnlockAccountsAsync())
                return;
        }

        var dialog = new PasswordDialog(
            mode == AccountStoreMode.PasswordLocked ? PasswordDialogMode.Change : PasswordDialogMode.Set,
            mode == AccountStoreMode.PasswordLocked ? new Func<string, bool>(_services.Store.VerifyPassword) : null)
        {
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _services.Repo.Save(dialog.NewPassword, bypassCountCheck: true);
        _services.Store.SetSessionPassword(dialog.NewPassword);
        UpdatePasswordSection();
    }

    private async void RemovePassword_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new PasswordDialog(PasswordDialogMode.Remove, _services!.Store.VerifyPassword)
        {
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        // The current password was verified inside the dialog; drop the session password
        // first so the save falls back to DPAPI instead of re-locking with the old one.
        _services.Store.SetSessionPassword(null);
        _services.Repo.Save(bypassCountCheck: true);
        UpdatePasswordSection();
    }
}