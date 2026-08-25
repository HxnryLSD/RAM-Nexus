using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RAM.Core.Roblox;
using RAM.Core.Roblox.Rdd;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace RAM.Views;

/// <summary>One installed RDD deployment, shown in the installed list.</summary>
public sealed class RddInstallInfo : INotifyPropertyChanged
{
    public string Version { get; init; } = "";
    public string Tag { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public string InstalledText { get; init; } = "";
    public SolidColorBrush TagChipBackground { get; init; } = new(Color.FromArgb(0, 0, 0, 0));
    public SolidColorBrush TagChipForeground { get; init; } = new(Color.FromArgb(0, 0, 0, 0));

    /// <summary>The app's active deployment (Accounts Launch + Fast Flags target).</summary>
    public bool IsActive { get; init; }
    public SolidColorBrush ActiveChipBackground { get; init; } = new(Color.FromArgb(0, 0, 0, 0));
    public SolidColorBrush ActiveChipForeground { get; init; } = new(Color.FromArgb(0, 0, 0, 0));
    public Visibility ActiveChipVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SetActiveVisibility => IsActive ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Folder size, computed in the background — starts as a placeholder.</summary>
    private string _sizeText = "…";
    public string SizeText
    {
        get => _sizeText;
        set
        {
            if (_sizeText == value) return;
            _sizeText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum StatusKind
{
    Info,
    Success,
    Error
}

public sealed partial class RddPage : Page
{
    private readonly WeaoRddApiClient _weao = new();
    private readonly InstallManager _manager = new();
    private CancellationTokenSource? _cts;
    private bool _installing;
    private string? _currentVersion;
    private string? _previousVersion;

    /// <summary>Bumped on every list refresh so in-flight size computations for a rebuilt list are dropped.</summary>
    private int _sizeComputeVersion;

    private AppServices? _services;

    /// <summary>Folder RDD installs live in (persisted in settings; default: the AppData data root).</summary>
    private string InstallRoot =>
        _services!.Settings.Get(RddOptions.SettingsKey, RddOptions.DefaultRoot);

    /// <summary>Settings key remembering the last place launched from this page.</summary>
    private const string LastPlaceIdKey = "RDDLastPlaceId";

    // Status feedback colors.
    private static readonly SolidColorBrush InfoBrush = new(Color.FromArgb(0xCC, 0xD6, 0xD6, 0xD6));
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromArgb(0xFF, 0x6B, 0xCB, 0x77));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(0xFF, 0xFF, 0x7B, 0x72));

    // Tag chip colors — Default installs get a blue chip, exploits a violet one.
    private static readonly Color DefaultTagBg = Color.FromArgb(0xFF, 0x2A, 0x3B, 0x55);
    private static readonly Color DefaultTagFg = Color.FromArgb(0xFF, 0x9F, 0xC6, 0xFF);
    private static readonly Color ExploitTagBg = Color.FromArgb(0xFF, 0x3A, 0x2A, 0x55);
    private static readonly Color ExploitTagFg = Color.FromArgb(0xFF, 0xC9, 0xA7, 0xFF);

    // Exploit freshness chip colors.
    private static readonly Color CurrentBg = Color.FromArgb(0xFF, 0x1E, 0x3A, 0x2A);
    private static readonly Color CurrentFg = Color.FromArgb(0xFF, 0x7E, 0xD9, 0x8C);
    private static readonly Color StaleBg = Color.FromArgb(0xFF, 0x3A, 0x30, 0x1E);
    private static readonly Color StaleFg = Color.FromArgb(0xFF, 0xE8, 0xC2, 0x6A);
    private static readonly Color OldBg = Color.FromArgb(0xFF, 0x3A, 0x20, 0x20);
    private static readonly Color OldFg = Color.FromArgb(0xFF, 0xF2, 0x8B, 0x82);

    public RddPage()
    {
        this.InitializeComponent();
        InstallsList.ItemsSource = Installs;
    }

    /// <summary>True while the page initializes (programmatic control values, not user intent).</summary>
    private bool _loading = true;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (AppServices)e.Parameter;
        RootBox.Text = InstallRoot;
        RootHintText.Text = $"Default: {RddOptions.DefaultRoot}";
        ParallelDownloadsToggle.IsOn = _services.Settings.Get(RddOptions.ParallelDownloadsKey, true);
        _loading = false; // from here on, toggle changes are user intent
        _ = LoadExploitsAsync();
        RefreshInstalls();

        _services.ClientUpdater.StateChanged += RefreshDefaultUpdateStatus;
        RefreshDefaultUpdateStatus();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (_services is not null)
            _services.ClientUpdater.StateChanged -= RefreshDefaultUpdateStatus;
    }

    /// <summary>
    /// Show the background Default-client updater's status under the installed list, and
    /// refresh the list once an update settles (a new Default folder may have appeared).
    /// </summary>
    private void RefreshDefaultUpdateStatus()
    {
        bool show = _services!.ClientUpdater.UpdateInProgress || !string.IsNullOrEmpty(_services.ClientUpdater.Status);
        DefaultUpdateStatus.Text = _services.ClientUpdater.Status;
        DefaultUpdateStatus.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!_services.ClientUpdater.UpdateInProgress)
            RefreshInstalls();
    }

    private ObservableCollection<RddInstallInfo> Installs { get; } = new();

    // ---- Exploit list ----

    private async Task LoadExploitsAsync(bool forceRefresh = false)
    {
        ExploitBox.Items.Add(new ComboBoxItem { Content = "Default — latest live build", Tag = null });

        try
        {
            // Page loads are stale-tolerant (the client serves its cached copy and revalidates
            // in the background); the explicit Refresh button forces a fresh fetch.
            var snapshot = await _weao.GetVersionSnapshotAsync(forceRefresh);
            var exploits = await _weao.GetExploitsAsync(forceRefresh);
            _currentVersion = snapshot.WindowsCurrent;
            _previousVersion = snapshot.WindowsPrevious;

            // The client returns the reference-filtered/sorted list (hidden + type + executor
            // order). This app only downloads Windows player builds, so drop Mac-platform
            // exploits — the same platform filter the reference page applies for Windows types.
            foreach (var e in exploits.Where(e => !string.Equals(e.Platform, "Mac", StringComparison.OrdinalIgnoreCase)))
            {
                string suffix = e.ComputeStatus(snapshot.WindowsCurrent, snapshot.WindowsPrevious) switch
                {
                    ExploitStatus.Stale => " (previous version)",
                    ExploitStatus.Old => " (outdated)",
                    _ => ""
                };
                ExploitBox.Items.Add(new ComboBoxItem { Content = e.Title + suffix, Tag = e });
            }
        }
        catch
        {
            SetStatus("Could not reach the exploit list (weao.gg is Cloudflare-protected). Only 'Default' (latest version) is available.", StatusKind.Error);
        }

        ExploitBox.SelectedIndex = 0;
    }

    private async void RefreshExploits_Click(object sender, RoutedEventArgs e)
    {
        ExploitBox.Items.Clear();
        _currentVersion = null;
        _previousVersion = null;
        await LoadExploitsAsync(forceRefresh: true);
    }

    private void ExploitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExploitBox.SelectedItem is ComboBoxItem item)
            UpdateExploitInfo(item.Tag as Exploit);
    }

    /// <summary>Describe the selected exploit (or Default) below the picker.</summary>
    private void UpdateExploitInfo(Exploit? exploit)
    {
        ExploitWebsiteLink.Visibility = Visibility.Collapsed;
        ExploitDiscordLink.Visibility = Visibility.Collapsed;

        if (exploit is null)
        {
            ExploitInfoTitle.Text = "Default";
            SetExploitChip("Latest live build", DefaultTagBg, DefaultTagFg);
            ExploitInfoDetail.Text = "Downloads the newest WindowsPlayer build published by Roblox.";
        }
        else
        {
            ExploitInfoTitle.Text = exploit.Title;
            var status = exploit.ComputeStatus(_currentVersion, _previousVersion);
            var (label, bg, fg) = status switch
            {
                ExploitStatus.Stale => ("Previous version", StaleBg, StaleFg),
                ExploitStatus.Old => ("Outdated", OldBg, OldFg),
                _ => ("Current", CurrentBg, CurrentFg)
            };
            SetExploitChip(label, bg, fg);

            string version = string.IsNullOrEmpty(exploit.RbxVersion) ? "the latest build" : $"version {exploit.RbxVersion}";
            string cost = string.IsNullOrEmpty(exploit.Cost) ? "" : $" · {exploit.Cost}";
            ExploitInfoDetail.Text = $"Requires Roblox {version}{cost}.";
            ExploitWebsiteLink.Content = "Website";
            ExploitWebsiteLink.NavigateUri = TryParseUri(exploit.WebsiteLink);
            ExploitWebsiteLink.Visibility = ExploitWebsiteLink.NavigateUri is null ? Visibility.Collapsed : Visibility.Visible;
            ExploitDiscordLink.Content = "Discord";
            ExploitDiscordLink.NavigateUri = TryParseUri(exploit.DiscordLink);
            ExploitDiscordLink.Visibility = ExploitDiscordLink.NavigateUri is null ? Visibility.Collapsed : Visibility.Visible;
        }

        ExploitInfoPanel.Visibility = Visibility.Visible;
    }

    private void SetExploitChip(string text, Color background, Color foreground)
    {
        ExploitStatusText.Text = text;
        ExploitStatusChip.Background = new SolidColorBrush(background);
        ExploitStatusText.Foreground = new SolidColorBrush(foreground);
    }

    // ---- Install folder ----

    private async void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            CommitButtonText = "Select folder"
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(_services!.RootWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        RootBox.Text = folder.Path;
        PersistRoot();
        RefreshInstalls();
    }

    private void PersistRoot()
    {
        var value = RootBox.Text.Trim();
        if (string.IsNullOrEmpty(value))
        {
            RootBox.Text = InstallRoot;
            return;
        }
        if (!string.Equals(value, InstallRoot, StringComparison.OrdinalIgnoreCase))
        {
            _services!.Settings.Set(RddOptions.SettingsKey, value);
            _services.Settings.Save();
        }
    }

    private void RootBox_LostFocus(object sender, RoutedEventArgs e)
    {
        PersistRoot();
        RefreshInstalls();
    }

    private void RootBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        DownloadButton_Click(sender, e);
    }

    private void ParallelDownloadsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services!.Settings.Set(RddOptions.ParallelDownloadsKey, ParallelDownloadsToggle.IsOn ? "true" : "false");
        _services.Settings.Save();
    }

    private void ExploitBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        DownloadButton_Click(sender, e);
    }

    // ---- Install ----

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        string root = RootBox.Text.Trim();
        if (string.IsNullOrEmpty(root))
        {
            root = InstallRoot;
            RootBox.Text = root;
        }

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex)
        {
            SetStatus($"Cannot create install folder '{root}': {ex.Message}", StatusKind.Error);
            return;
        }

        PersistRoot();

        var exploit = (ExploitBox.SelectedItem as ComboBoxItem)?.Tag as Exploit;
        await RunInstallAsync(root, exploit?.Title, exploit, force: false, parallelDownloads: ParallelDownloadsToggle.IsOn);
    }

    private void Redownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RddInstallInfo info) return;
        string root = Path.GetDirectoryName(info.FolderPath) ?? InstallRoot;
        var exploit = ExploitBox.Items.OfType<ComboBoxItem>()
            .Select(item => item.Tag as Exploit)
            .FirstOrDefault(x => x is not null && string.Equals(x.Title, info.Tag, StringComparison.OrdinalIgnoreCase));
        _ = RunInstallAsync(root, info.Tag, exploit, force: true, parallelDownloads: ParallelDownloadsToggle.IsOn);
    }

    /// <summary>Play: just start the Roblox player from this deployment (no place).</summary>
    private void Play_Click(SplitButton sender, SplitButtonClickEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not RddInstallInfo info) return;

        try
        {
            var launcher = new RobloxLauncher();
            var process = launcher.LaunchPlayer(info.FolderPath);
            SetStatus(process is null
                ? $"Could not launch {info.Tag} — no Roblox player executable found in this deployment."
                : $"Launched the Roblox Player ({info.Version}).",
                process is null ? StatusKind.Error : StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to launch {info.Tag}: {ex.Message}", StatusKind.Error);
        }
    }

    /// <summary>Play with Place ID: launch this deployment into a specific place (optionally a JobId).</summary>
    private async void PlayWithPlaceId_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RddInstallInfo info) return;

        var result = await PlaceLaunchPrompt.AskAsync(
            XamlRoot, $"Play — {info.Tag}", "Play",
            _services!.Settings.Get(LastPlaceIdKey, 0L), ErrorBrush);
        if (result is null) return;

        long placeId = result.Value.PlaceId;
        string? jobId = result.Value.JobId;
        _services!.Settings.Set(LastPlaceIdKey, placeId.ToString());
        _services.Settings.Save();

        try
        {
            var launcher = new RobloxLauncher();
            var process = launcher.LaunchPlace(info.FolderPath, placeId, jobId);
            SetStatus(process is null
                ? $"Could not launch {info.Tag} — no Roblox player executable found in this deployment."
                : $"Launched {info.Tag} ({info.Version}) to place {placeId}.",
                process is null ? StatusKind.Error : StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to launch {info.Tag}: {ex.Message}", StatusKind.Error);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RddInstallInfo info) return;

        var dialog = new ContentDialog
        {
            Title = "Delete deployment?",
            Content = $"Delete '{info.Tag}' ({info.Version})? This removes the whole Roblox installation folder — it cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            Directory.Delete(info.FolderPath, true);
            SetStatus($"Deleted {info.Tag} ({info.Version}).", StatusKind.Success);
        }
        catch (Exception ex)
        {
            SetStatus($"Could not delete {info.Tag}: {ex.Message}", StatusKind.Error);
        }

        RefreshInstalls();
    }

    /// <summary>Run an install and present the result. All orchestration lives in InstallManager.</summary>
    private async Task RunInstallAsync(string root, string? tag, Exploit? exploit, bool force, bool parallelDownloads)
    {
        if (_installing) return;
        _installing = true;

        DownloadButton.IsEnabled = false;
        InstallsList.IsHitTestVisible = false; // row Re-download/Delete can't run concurrently
        CancelButton.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        ProgressPercentText.Text = "";
        ProgressDetailText.Text = "";
        SetStatus("");

        _cts = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(p =>
        {
            if (p.Phase is InstallPhase.Downloading or InstallPhase.Extracting)
            {
                Progress.IsIndeterminate = false;
                Progress.Maximum = Math.Max(1, p.BytesTotal);
                Progress.Value = Math.Clamp(p.BytesDone, 0, p.BytesTotal);
                ProgressPercentText.Text = $"{(int)(p.BytesDone * 100.0 / Math.Max(1, p.BytesTotal))}%";
                ProgressDetailText.Text = $"{p.Message} · {FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)}";
            }
            else
            {
                Progress.IsIndeterminate = true;
                ProgressPercentText.Text = "";
                ProgressDetailText.Text = p.Message;
            }
        });

        try
        {
            // Serialize with the background Default-client updater: both sides hold the gate
            // while writing to the install root, so they can never race on the same folder.
            ProgressDetailText.Text = "Waiting for the background client update to finish…";
            try
            {
                await RddInstallGate.Semaphore.WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Download cancelled.", StatusKind.Info);
                return;
            }

            try
            {
                var result = await _manager.InstallAsync(root, tag, exploit, force, progress, _cts.Token, parallelDownloads);
                var kind = result.Kind;
                SetStatus(result.Message ?? "", kind switch
                {
                    InstallResultKind.Installed or InstallResultKind.Skipped => StatusKind.Success,
                    InstallResultKind.Cancelled => StatusKind.Info,
                    _ => StatusKind.Error
                });
            }
            finally
            {
                RddInstallGate.Semaphore.Release();
            }
            RefreshInstalls();
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            DownloadButton.IsEnabled = true;
            InstallsList.IsHitTestVisible = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
            _installing = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => _cts?.Cancel();

    /// <summary>Mark an install as the app's active deployment (Accounts Launch + Fast Flags target).</summary>
    private void SetActive_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RddInstallInfo info) return;

        RddActiveInstall.Set(_services!.Settings, info.FolderPath);
        SetStatus($"{info.Tag} is now the active install — Accounts Launch and Fast Flags will target it.", StatusKind.Success);
        RefreshInstalls();
    }

    // ---- Installed list ----

    private void RefreshInstalls()
    {
        Installs.Clear();
        int generation = ++_sizeComputeVersion;
        var store = new RddDeploymentStore(InstallRoot);
        string? activeFolder = RddActiveInstall.Resolve(_services!.Settings, store);
        foreach (var folder in store.ListInstalls())
        {
            var dir = new DirectoryInfo(folder);
            string tag = store.GetTag(folder) ?? "untagged";
            bool isDefault = string.Equals(tag, "Default", StringComparison.OrdinalIgnoreCase);
            var info = new RddInstallInfo
            {
                Version = dir.Name.StartsWith("version-", StringComparison.OrdinalIgnoreCase)
                    ? dir.Name["version-".Length..]
                    : dir.Name,
                Tag = tag,
                FolderPath = folder,
                InstalledText = FormatRelative(dir.LastWriteTime),
                TagChipBackground = new SolidColorBrush(isDefault ? DefaultTagBg : ExploitTagBg),
                TagChipForeground = new SolidColorBrush(isDefault ? DefaultTagFg : ExploitTagFg),
                IsActive = activeFolder is not null && string.Equals(folder, activeFolder, StringComparison.OrdinalIgnoreCase),
                ActiveChipBackground = new SolidColorBrush(CurrentBg),
                ActiveChipForeground = new SolidColorBrush(CurrentFg),
                SizeText = "…"
            };
            Installs.Add(info);
            _ = ComputeSizeAsync(info, generation);
        }

        bool any = Installs.Count > 0;
        InstallsList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        InstallsCountBadge.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        InstallsCountText.Text = Installs.Count.ToString();
    }

    // ---- Helpers ----

    /// <summary>Sum the bytes of every file under <paramref name="folder"/> on a background thread.</summary>
    private async Task ComputeSizeAsync(RddInstallInfo info, int generation)
    {
        long bytes = await Task.Run(() => ComputeFolderSize(info.FolderPath));
        if (generation != _sizeComputeVersion) return; // the list was rebuilt while we were walking
        info.SizeText = FormatBytes(bytes);
    }

    /// <summary>
    /// Total size of a folder, skipping unreadable directories and files that vanish or
    /// are locked mid-walk (e.g. while Roblox is running). Safe to call off the UI thread.
    /// </summary>
    private static long ComputeFolderSize(string folder)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                    pending.Push(sub);
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { /* file locked or deleted mid-walk */ }
                }
            }
            catch
            {
                // directory gone or access denied — skip it
            }
        }
        return total;
    }

    private void SetStatus(string message, StatusKind kind = StatusKind.Info)
    {
        StatusText.Text = message;
        StatusText.Foreground = kind switch
        {
            StatusKind.Success => SuccessBrush,
            StatusKind.Error => ErrorBrush,
            _ => InfoBrush
        };
    }

    private static Uri? TryParseUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private static string FormatRelative(DateTime when)
    {
        var elapsed = DateTime.Now - when;
        if (elapsed.TotalMinutes < 1) return "Installed just now";
        if (elapsed.TotalHours < 1) return $"Installed {(int)elapsed.TotalMinutes} min ago";
        if (elapsed.TotalDays < 1) return $"Installed {(int)elapsed.TotalHours} h ago";
        if (elapsed.TotalDays < 60)
        {
            int days = (int)elapsed.TotalDays;
            return $"Installed {days} day{(days == 1 ? "" : "s")} ago";
        }
        return $"Installed {when:dd MMM yyyy}";
    }
}
