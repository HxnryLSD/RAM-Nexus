using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using RAM.Core.Infrastructure;
using RAM.Core.Models;
using RAM.Core.Roblox.FastFlags;
using RAM.Dialogs;

namespace RAM;

/// <summary>
/// The app's service bundle — the "DI-light (manual service container)" the rewrite plan
/// promised. One instance is built in the composition root (App.OnLaunched) and handed to
/// every page through Frame navigation (pages read it from OnNavigatedTo's parameter), so
/// pages and shell services never reach into App statics. Also owns the vault session
/// state (unlock / lock and the <see cref="VaultLocked"/> event) that the pages and
/// AutoLockService share.
/// </summary>
public sealed class AppServices
{
    public AppServices(SettingsStore settings, AccountStore store, FastFlagStore fastFlags, MainWindow rootWindow)
    {
        Settings = settings;
        Store = store;
        FastFlags = fastFlags;
        RootWindow = rootWindow;
        Repo = new AccountRepository(store);

        AutoLock = new AutoLockService(rootWindow, settings, store, LockVault, () => UnlockAccountsAsync());
        ClientUpdater = new ClientUpdaterService(settings, fastFlags);
    }

    public SettingsStore Settings { get; }
    public AccountStore Store { get; }
    public FastFlagStore FastFlags { get; }
    public MainWindow RootWindow { get; }
    public AccountRepository Repo { get; }

    /// <summary>The in-memory account list (Repo.Accounts) the UI binds to.</summary>
    public ObservableCollection<Account> Accounts => Repo.Accounts;

    public AutoLockService AutoLock { get; }
    public ClientUpdaterService ClientUpdater { get; }

    /// <summary>Raised after <see cref="LockVault"/> so open pages can refresh their state.</summary>
    public event Action? VaultLocked;

    /// <summary>
    /// Load the account list. For a password-locked file this shows the unlock dialog
    /// (re-prompting on a wrong password) and keeps the entered password as the session
    /// password so later saves stay locked. Returns false when the user cancels instead of
    /// unlocking — the file stays locked and accounts stay empty.
    /// </summary>
    public async Task<bool> UnlockAccountsAsync()
    {
        if (Store.DetectMode() != AccountStoreMode.PasswordLocked)
        {
            Repo.Load();
            return true;
        }

        if (Store.SessionPassword is not null)
            return true; // already unlocked this session

        var dialog = new UnlockDialog(Store.VerifyPassword)
        {
            XamlRoot = RootWindow.Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return false;

        string password = dialog.Password!;
        Repo.Load(password);
        Store.SetSessionPassword(password);
        return true;
    }

    /// <summary>
    /// Re-lock the vault without restarting: drop the session password and clear the
    /// in-memory accounts so the UI shows nothing until the file is unlocked again.
    /// No-op unless the file is password-locked and currently unlocked. Used by the
    /// Settings page's "Lock now" button and by AutoLockService.
    /// </summary>
    public void LockVault()
    {
        if (Store.DetectMode() != AccountStoreMode.PasswordLocked || Store.SessionPassword is null)
            return;
        if (Accounts.Count == 0)
            return; // nothing to lock

        Store.SetSessionPassword(null);
        Accounts.Clear();
        VaultLocked?.Invoke();
    }
}
