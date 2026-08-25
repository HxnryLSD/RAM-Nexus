using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RAM.Core.Infrastructure;
using RAM.Core.Models;
using RAM.Core.Roblox;
using RAM.Core.Roblox.Rdd;
using RAM.Core.Security;
using RAM.Dialogs;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace RAM.Views;

public sealed partial class AccountsPage : Page
{
    /// <summary>Settings key remembering the last place launched into from this page.</summary>
    private const string LastPlaceIdKey = "AccountsLastPlaceId";

    // Status feedback colors.
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(0xFF, 0x6B, 0xCB, 0x77));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(0xFF, 0xFF, 0x7B, 0x72));

    private AppServices? _services;

    /// <summary>Folder RDD installs live in (same setting the RDD / Fast Flags pages use).</summary>
    private string InstallRoot =>
        _services!.Settings.Get(RddOptions.SettingsKey, RddOptions.DefaultRoot);

    public AccountsPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (AppServices)e.Parameter;

        // The list shows a search/group-filtered mirror of the accounts; keep it in sync
        // when accounts are added/removed anywhere (this page, Lock now, …).
        _services.Accounts.CollectionChanged += Accounts_CollectionChanged;
        RebuildGroups();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_services is not null)
            _services.Accounts.CollectionChanged -= Accounts_CollectionChanged;
    }

    private void Accounts_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RebuildGroups();

    public ObservableCollection<Account> Accounts => _services!.Accounts;

    /// <summary>The search-filtered accounts shown by the list (source of truth stays the repository's Accounts).</summary>
    public ObservableCollection<Account> FilteredAccounts { get; } = new();

    /// <summary>The group chips shown above the list (one per distinct group, plus All).</summary>
    public ObservableCollection<GroupChip> GroupChips { get; } = new();

    private string _searchText = "";

    /// <summary>Active group filter — null shows every group (All).</summary>
    private string? _selectedGroup;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text.Trim();
        RebuildFiltered();
    }

    /// <summary>Rebuild the group chips from the accounts, keeping the active filter when possible.</summary>
    private void RebuildGroups()
    {
        var groupNames = Accounts
            .Select(a => string.IsNullOrEmpty(a.Group) ? "Default" : a.Group)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The filtered group may have disappeared (last account removed, or a rename via
        // the edit dialog) — fall back to All instead of showing an empty list.
        if (_selectedGroup is not null && !groupNames.Contains(_selectedGroup, StringComparer.OrdinalIgnoreCase))
            _selectedGroup = null;

        GroupChips.Clear();
        foreach (var name in groupNames)
            GroupChips.Add(new GroupChip(name));

        RefreshChipStates();
        RebuildFiltered();
    }

    /// <summary>Sync the chips' checked state to the active filter.</summary>
    private void RefreshChipStates()
    {
        AllGroupsChip.IsChecked = _selectedGroup is null;
        foreach (var chip in GroupChips)
            chip.IsSelected = string.Equals(chip.Name, _selectedGroup, StringComparison.OrdinalIgnoreCase);
    }

    private void AllGroupsChip_Click(object sender, RoutedEventArgs e)
    {
        _selectedGroup = null;
        RefreshChipStates();
        RebuildFiltered();
    }

    private void GroupChip_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupChip chip)
            return;

        // Clicking the active chip again clears the filter (back to All).
        _selectedGroup = chip.IsSelected ? chip.Name : null;
        RefreshChipStates();
        RebuildFiltered();
    }

    private void RebuildFiltered()
    {
        FilteredAccounts.Clear();
        foreach (var account in Accounts)
            if (Matches(account))
                FilteredAccounts.Add(account);
    }

    private bool Matches(Account account)
    {
        if (_selectedGroup is not null &&
            !string.Equals(string.IsNullOrEmpty(account.Group) ? "Default" : account.Group, _selectedGroup, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrEmpty(_searchText)) return true;

        bool Contains(string? value) =>
            value is not null && value.Contains(_searchText, StringComparison.OrdinalIgnoreCase);

        return Contains(account.DisplayName)
            || Contains(account.Username)
            || Contains(account.Alias)
            || Contains(account.Group)
            || Contains(account.UserID.ToString());
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddAccountDialog
        {
            XamlRoot = this.Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        var toAdd = dialog.Accounts;
        if (toAdd.Count == 0)
            return;

        // Resolve usernames for any account added without one (bulk pastes, cookie mode) —
        // concurrently, so one dead cookie doesn't stall the rest.
        var accounts = new Account[toAdd.Count];
        await Task.WhenAll(toAdd.Select(async (entry, i) =>
        {
            var account = new Account
            {
                Username = entry.Username,
                SecurityToken = entry.Cookie,
                Valid = false
            };
            if (string.IsNullOrEmpty(account.Username))
                await ResolveAccountInfoAsync(account);
            accounts[i] = account;
        }));

        // Save first, then surface the accounts — if the save is refused (password-locked
        // without a session password), nothing is shown as added.
        if (!_services!.Repo.Add(accounts))
        {
            SetStatus("Accounts are locked — unlock them to add accounts.", StatusKind.Info);
            return;
        }

        SetStatus(toAdd.Count == 1
            ? $"Added {accounts[0].DisplayName}"
            : $"Added {toAdd.Count} accounts", StatusKind.Info);
    }

    // ---- Vault import / export ----

    private const string VaultFileExtension = ".ramvault";

    /// <summary>Export the vault to a user-picked, password-encrypted backup file.</summary>
    private async void ExportVault_Click(object sender, RoutedEventArgs e)
    {
        if (Accounts.Count == 0)
        {
            SetStatus("Nothing to export — add accounts first.", StatusKind.Info);
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "ram-accounts-vault"
        };
        picker.FileTypeChoices.Add("Encrypted account backup", new List<string> { VaultFileExtension });
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_services!.RootWindow));

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var dialog = new PasswordDialog(PasswordDialogMode.Set, title: "Backup password", primaryButtonText: "Export")
        {
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            byte[] payload = VaultTransfer.Export(Accounts.ToList(), dialog.NewPassword!);
            await File.WriteAllBytesAsync(file.Path, payload);
            SetStatus($"Exported {Accounts.Count} account(s) to {file.Name}.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Export failed: {ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>Restore accounts from a user-picked encrypted backup, merging into the vault.</summary>
    private async void ImportVault_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(VaultFileExtension);
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_services!.RootWindow));

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        byte[] data = await File.ReadAllBytesAsync(file.Path);

        // Only password-encrypted vaults need a password — upstream AccountData.json files
        // (DPAPI or plaintext) decrypt without one.
        string password = "";
        if (Cryptography.HasRAMHeader(data))
        {
            var dialog = new UnlockDialog(title: "Import backup", primaryButtonText: "Import")
            {
                XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            password = dialog.Password!;
        }

        try
        {
            List<Account> imported = VaultTransfer.Import(data, password);
            var merged = Accounts.ToList();
            var (added, skipped) = VaultTransfer.Merge(merged, imported);

            if (added == 0)
            {
                SetStatus(skipped > 0
                    ? "Nothing new to import — every account in the backup is already in the vault."
                    : "The backup contains no usable accounts.", StatusKind.Info);
                return;
            }

            // Save first, then surface the merged list (same policy as Add account).
            if (!_services!.Repo.SaveAndReplace(merged))
            {
                SetStatus("Accounts are locked — unlock them to import.", StatusKind.Info);
                return;
            }

            SetStatus(skipped > 0
                ? $"Imported {added} account(s) from {file.Name} ({skipped} duplicate/empty skipped)."
                : $"Imported {added} account(s) from {file.Name}.", StatusKind.Success);
        }
        catch (System.Security.Cryptography.CryptographicException ex) when (ex.Message.Contains("another PC", StringComparison.Ordinal))
        {
            SetStatus(ex.Message, StatusKind.Error);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            SetStatus("Could not decrypt the backup — wrong password or the file is not a valid encrypted vault.", StatusKind.Error);
        }
        catch (Exception ex)
        {
            SetStatus($"Import failed: {ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>Best-effort fetch of the username + user id for an account from its cookie.</summary>
    private static async Task ResolveAccountInfoAsync(Account account)
    {
        try
        {
            using var client = new RobloxApiClient(account.SecurityToken);
            var (username, userId, _) = await client.GetAccountInfoAsync();
            if (!string.IsNullOrEmpty(username))
            {
                account.Username = username;
                account.Valid = true;
            }
            if (userId is > 0)
                account.UserID = userId.Value;
        }
        catch
        {
            // Cookie invalid / network down — keep the account as pasted; the user can
            // remove it or fix the cookie later.
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_services!.Repo.Save(bypassCountCheck: true))
        {
            SetStatus("Accounts are locked — unlock them to save changes.", StatusKind.Info);
            return;
        }
        SetStatus($"Saved {Accounts.Count} account(s)", StatusKind.Info);
    }

    private void AccountsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        bool canLaunch = AccountsList.SelectedItem is Account a && !string.IsNullOrEmpty(a.SecurityToken);
        LaunchButton.IsEnabled = canLaunch;
        RemoveAccountButton.IsEnabled = AccountsList.SelectedItem is not null;
    }

    // ---- Account-based launching ----

    /// <summary>Primary Launch: rejoin the last-used place for the account, or ask for one.</summary>
    private async void Launch_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if (AccountsList.SelectedItem is not Account account)
            return;

        long last = _services!.Settings.Get(LastPlaceIdKey, 0L);
        if (last > 0)
            await LaunchAccountAsync(account, last, jobId: null);
        else
            await LaunchWithPlaceIdAsync(account);
    }

    private async void LaunchWithPlaceId_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not Account account)
            return;
        await LaunchWithPlaceIdAsync(account);
    }

    /// <summary>Ask for a place (and optional job) and launch the account into it.</summary>
    private async Task LaunchWithPlaceIdAsync(Account account)
    {
        var result = await PlaceLaunchPrompt.AskAsync(
            XamlRoot, $"Launch as {account.DisplayName}", "Launch",
            _services!.Settings.Get(LastPlaceIdKey, 0L), ErrorBrush);
        if (result is null) return;

        await LaunchAccountAsync(account, result.Value.PlaceId, result.Value.JobId);
    }

    /// <summary>
    /// Launch the Roblox player as <paramref name="account"/> into a place: resolve the
    /// RDD install to use, fetch an auth ticket with the account's .ROBLOSECURITY cookie,
    /// and start the player with that ticket. Uses only RDD installs, never the normal
    /// Roblox installation.
    /// </summary>
    private async Task LaunchAccountAsync(Account account, long placeId, string? jobId)
    {
        if (string.IsNullOrEmpty(account.SecurityToken))
        {
            SetStatus($"{account.DisplayName} has no security cookie — remove and re-add the account to launch it.", StatusKind.Error);
            return;
        }

        var store = new RddDeploymentStore(InstallRoot);
        string? versionFolder = RddActiveInstall.Resolve(_services!.Settings, store);
        if (versionFolder is null)
        {
            SetStatus("No RDD install found. Download a deployment on the RDD page first — only RDD installs are used, never the normal Roblox installation.", StatusKind.Error);
            return;
        }

        // Roblox ties a launch to a browser tracker id; keep a stable one per account
        // (upstream behaviour) so relaunches are recognized as the same session.
        if (string.IsNullOrEmpty(account.BrowserTrackerID))
        {
            var random = new Random();
            account.BrowserTrackerID = random.Next(100000, 175000).ToString() + random.Next(100000, 900000).ToString();
        }

        try
        {
            using var client = new RobloxApiClient(account.SecurityToken);
            string? ticket = await client.GetAuthTicketAsync();
            if (ticket is null)
            {
                SetStatus($"Could not authenticate {account.DisplayName} — the security cookie may be expired or invalid. Remove and re-add the account.", StatusKind.Error);
                return;
            }

            var launcher = new RobloxLauncher();
            var process = launcher.LaunchPlaceAsAccount(versionFolder, ticket, placeId, jobId, account.BrowserTrackerID);

            // A successful launch proves the cookie works and counts as use of the account.
            account.LastUse = DateTime.Now;
            account.Valid = true;
            bool saved = _services!.Repo.Save();
            _services.Settings.Set(LastPlaceIdKey, placeId.ToString());
            _services.Settings.Save();

            if (process is null)
                SetStatus($"Could not launch {account.DisplayName} — no Roblox player executable found in this deployment.", StatusKind.Error);
            else if (!saved)
                SetStatus($"Launched {account.DisplayName} into place {placeId} (couldn't save last-use — accounts are locked).", StatusKind.Success);
            else
                SetStatus($"Launched {account.DisplayName} into place {placeId}.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to launch {account.DisplayName}: {ex.Message}", StatusKind.Error);
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        if (kind == StatusKind.Info)
            StatusText.ClearValue(TextBlock.ForegroundProperty); // theme default (page's usual look)
        else
            StatusText.Foreground = kind == StatusKind.Success ? SuccessBrush : ErrorBrush;
    }

    // ---- Editing ----

    /// <summary>Double-click an account row to open the vault editor.</summary>
    private async void AccountsList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Account account)
            return;
        await EditAccountAsync(account);
    }

    /// <summary>Apply the dialog's vault fields to the account and persist (save first, then surface).</summary>
    private async Task EditAccountAsync(Account account)
    {
        var dialog = new EditAccountDialog(account)
        {
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            // The repository persists first and rolls the account back if the store refuses
            // (vault locked) — the same policy as Add / Remove, in one place.
            if (!_services!.Repo.Update(account, () =>
            {
                account.Alias = dialog.Alias;
                account.Group = dialog.Group;
                account.Description = dialog.Notes;
                account.Password = dialog.StoredPassword;
            }))
            {
                SetStatus("Accounts are locked — unlock them to edit accounts.", StatusKind.Info);
                return;
            }
        }
        catch (ArgumentOutOfRangeException ex)
        {
            SetStatus($"Could not save {account.DisplayName}: {ex.Message}", StatusKind.Error);
            return;
        }

        // Rows bind with x:Bind OneTime and chips mirror the groups — rebuild both so the
        // new alias/group/indicators show (a group rename also updates the chip row).
        RebuildGroups();
        SetStatus($"Saved {account.DisplayName}", StatusKind.Success);
    }

    /// <summary>
    /// One group chip: a name plus its checked state, bound TwoWay so clicking a chip
    /// updates it directly and the filter handler reads the new state back.
    /// </summary>
    public sealed class GroupChip : INotifyPropertyChanged
    {
        public GroupChip(string name) => Name = name;

        public string Name { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private async void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not Account account)
            return;

        var dialog = new ContentDialog
        {
            Title = "Remove account?",
            Content = $"Remove '{account.DisplayName}'? This cannot be undone.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        // The repository persists the removal first (bypassCountCheck so deleting the last
        // account still persists the empty list) and leaves the list untouched on refusal.
        if (!_services!.Repo.Remove(account))
        {
            SetStatus("Accounts are locked — unlock them to remove accounts.", StatusKind.Info);
            return;
        }
        SetStatus($"Removed {account.DisplayName}", StatusKind.Info);
    }
}
