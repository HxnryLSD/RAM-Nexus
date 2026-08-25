using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RAM.Core;
using RAM.Core.Updates;

namespace RAM.Views;

public sealed partial class AboutPage : Page
{
    private AppServices? _services;
    private UpdateService? _updates;

    private UpdateInfo? _available;
    private string? _downloadedZip;
    private string? _stagedDir;
    private CancellationTokenSource? _cts;

    public AboutPage()
    {
        this.InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        VersionText.Text = $"Version {version} · .NET {Environment.Version}";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (AppServices)e.Parameter;
        _updates = new UpdateService(
            manifestUrl: _services.Settings.Get("UpdateManifestUrl", UpdateService.DefaultManifestUrl));
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateStatus.Text = "Checking for updates…";

        try
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            _available = await _updates!.CheckAsync(current);
            _downloadedZip = null;
            _stagedDir = null;
            RestartButton.IsEnabled = false;

            if (_available is null)
            {
                UpdateStatus.Text = "You're on the latest version.";
            }
            else
            {
                UpdateStatus.Text = $"Update available: {_available.Version} ({_available.TagName}).";
                DownloadButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = $"Update check failed: {ex.Message}";
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_available is null) return;

        DownloadButton.IsEnabled = false;
        CheckButton.IsEnabled = false;
        CancelUpdateButton.Visibility = Visibility.Visible;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Maximum = 100;
        UpdateProgress.Value = 0;

        _cts = new CancellationTokenSource();
        var progress = new Progress<UpdateProgress>(p =>
        {
            if (p.BytesTotal > 0)
            {
                UpdateProgress.Value = (double)p.BytesDone / p.BytesTotal * 100;
                UpdateStatus.Text = $"Downloading update… {p.BytesDone / 1024.0 / 1024.0:0.0} / {p.BytesTotal / 1024.0 / 1024.0:0.0} MB";
            }
            else
            {
                UpdateStatus.Text = "Downloading update…";
            }
        });

        try
        {
            var dir = AppPaths.Updates;
            _downloadedZip = await _updates!.DownloadAsync(_available, dir, progress, _cts.Token);

            // Extract next to the app folder (never over it), so a corrupt package or a crash
            // mid-extract can't damage the running install. Restart then swaps it into place.
            UpdateStatus.Text = "Preparing update…";
            UpdateProgress.Visibility = Visibility.Visible;
            UpdateProgress.IsIndeterminate = true;
            _stagedDir = await UpdateService.StageUpdateAsync(_downloadedZip, _available.Version, ct: _cts.Token);
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateStatus.Text = $"Update {_available.Version} is ready. Restart the app to install.";
            RestartButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Text = "Update download cancelled.";
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = $"Update download failed: {ex.Message}";
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            CancelUpdateButton.Visibility = Visibility.Collapsed;
            UpdateProgress.Visibility = Visibility.Collapsed;
            DownloadButton.IsEnabled = _available is not null;
            CheckButton.IsEnabled = true;
        }
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadedZip is null || _stagedDir is null) return;

        // Detach a helper that waits for this process to exit, swaps the staged folder into
        // the app folder (rename-based, so removed files actually go away), and relaunches.
        UpdateService.ApplyAndRestart(_downloadedZip, _stagedDir);
        Application.Current.Exit();
    }

    private void CancelUpdateButton_Click(object sender, RoutedEventArgs e)
        => _cts?.Cancel();
}
