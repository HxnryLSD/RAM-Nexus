using Microsoft.UI.Xaml;
using RAM.Core.Infrastructure;
using RAM.Core.Roblox.FastFlags;
using RAM.Core.Roblox.Rdd;

namespace RAM;

/// <summary>
/// Keeps the Default (latest live build) RDD install current in the background: checks
/// Roblox's channel API at startup and on a timer and, when a newer build exists,
/// downloads it under the Default tag and re-applies fast flags + FPS unlock. The RDD
/// page subscribes to <see cref="StateChanged"/> to surface the status.
/// No-ops unless the Default install already exists (the RDD page bootstraps it) and the
/// auto-update toggle (Settings → Roblox) is on.
/// </summary>
public sealed class ClientUpdaterService : IDisposable
{
    /// <summary>How often to re-check for a new live build while the app runs.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(60);

    private readonly SettingsStore _settings;
    private readonly FastFlagStore _fastFlags;
    private readonly ClientUpdater _updater = new();
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;
    private bool _running;

    /// <summary>Latest user-facing status; empty when idle with nothing to report.</summary>
    public string Status { get; private set; } = "";

    /// <summary>True while a Default client update is downloading/installing.</summary>
    public bool UpdateInProgress { get; private set; }

    /// <summary>Raised whenever <see cref="Status"/> or <see cref="UpdateInProgress"/> changes.</summary>
    public event Action? StateChanged;

    public ClientUpdaterService(SettingsStore settings, FastFlagStore fastFlags)
    {
        _settings = settings;
        _fastFlags = fastFlags;
        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += async (_, _) => await RunOnceAsync();
        _timer.Start();
    }

    /// <summary>Check for a newer live build and update the Default install if needed.</summary>
    public async Task RunOnceAsync()
    {
        if (_running) return;
        if (!_settings.Get(RddOptions.AutoUpdateKey, true))
        {
            SetStatus("");
            return;
        }

        _running = true;
        try
        {
            string root = _settings.Get(RddOptions.SettingsKey, RddOptions.DefaultRoot);
            var info = await _updater.CheckAsync(root);
            if (info is null)
            {
                SetStatus(""); // no Default install yet — the RDD page bootstraps it
                return;
            }

            if (!info.Available)
            {
                SetStatus($"Default client is up to date — {ShortVersion(info.LiveVersion)}.");
                return;
            }

            await UpdateAsync(root, info);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Default client update cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Default client update failed: {ex.Message}");
        }
        finally
        {
            _running = false;
        }
    }

    private async Task UpdateAsync(string root, ClientUpdateInfo info)
    {
        SetUpdateState(true, $"New build available — updating Default to {ShortVersion(info.LiveVersion)}…");
        _cts = new CancellationTokenSource();
        try
        {
            // Progress<T> is created on the UI thread, so callbacks marshal back here.
            var progress = new Progress<InstallProgress>(p =>
            {
                if (p.Phase is InstallPhase.Downloading or InstallPhase.Extracting && !string.IsNullOrEmpty(p.Message))
                    SetStatus($"Updating Default to {ShortVersion(info.LiveVersion)} — {p.Message}");
            });

            // Wait for any manual RDD install to finish (and vice versa) — both sides hold
            // the gate while touching the install root.
            await RddInstallGate.Semaphore.WaitAsync(_cts.Token);
            try
            {
                var result = await _updater.UpdateAsync(
                    root, _settings, _fastFlags, progress, _cts.Token,
                    _settings.Get(RddOptions.ParallelDownloadsKey, true));
                SetStatus(result.Install.Kind switch
                {
                    InstallResultKind.Installed => $"Default client updated to {ShortVersion(info.LiveVersion)}.",
                    InstallResultKind.Skipped => $"Default client is current ({ShortVersion(info.LiveVersion)}).",
                    InstallResultKind.Cancelled => "Default client update cancelled.",
                    _ => $"Default client update failed: {result.Install.Message}"
                });
            }
            finally
            {
                RddInstallGate.Semaphore.Release();
            }
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetUpdateState(false, Status);
        }
    }

    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        StateChanged?.Invoke();
    }

    private void SetUpdateState(bool inProgress, string status)
    {
        bool changed = inProgress != UpdateInProgress || Status != status;
        UpdateInProgress = inProgress;
        Status = status;
        if (changed) StateChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Strip the "version-" prefix for display ("version-abc123" → "abc123").</summary>
    private static string ShortVersion(string folderName)
        => folderName.StartsWith("version-", StringComparison.OrdinalIgnoreCase)
            ? folderName["version-".Length..]
            : folderName;
}
