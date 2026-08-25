using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using RAM.Core;
using RAM.Core.Infrastructure;
using RAM.Core.Roblox.FastFlags;
using RAM.Core.Updates;
using RAM.Dialogs;

namespace RAM;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // Crash handling: everything is logged to crash.log; fatal sources also show the
        // polite error dialog (with copy-stack) instead of a silent exit.
        CrashLog.Install();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            CrashLog.Write("Unhandled exception (process terminating)", ex);
            FatalErrorDialog.ShowBlocking(_window, ex); // best effort — the process dies after this
        };
        DebugSettings.BindingFailed += (_, e) => CrashLog.Write($"XAML binding failed: {e.Message}", null);
        DebugSettings.XamlResourceReferenceFailed += (_, e) => CrashLog.Write($"XAML resource reference failed: {e.Message}", null);
    }

    /// <summary>
    /// XAML-thread unhandled exception: log it, then keep the app alive and show the
    /// polite dialog (marked handled BEFORE the await — the event framework reads the flag
    /// as soon as the handler yields, so marking it after the dialog would be too late).
    /// With no window yet (startup crash) the app is allowed to die and the
    /// AppDomain handler shows the fallback dialog instead.
    /// </summary>
    private async void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        CrashLog.Write("Unhandled XAML exception", e.Exception);

        if (_window?.Content.XamlRoot is not XamlRoot xamlRoot)
            return; // startup crash before the window exists — let AppDomain handle the dialog

        e.Handled = true; // keep the app running so the dialog can appear
        await FatalErrorDialog.ShowAsync(xamlRoot, e.Exception);
    }

    /// <summary>
    /// Composition root: builds every service once, bundles them into an
    /// <see cref="AppServices"/> handed to pages through frame navigation, and starts the
    /// shell services. Nothing here stays reachable as a static — pages and services get
    /// their dependencies injected.
    /// </summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // A secondary activation of an already-running instance must not re-run setup:
        // reloading accounts into the repository would duplicate every entry.
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        // Settings → Apps uninstall entry points here: remove the shortcut + registry entry,
        // then schedule deletion of this install folder once the process has exited.
        if (Environment.GetCommandLineArgs().Contains("--uninstall"))
        {
            UninstallSelf();
            Environment.Exit(0);
        }

        // Move data files from the old exe-folder anchor into the AppData data root
        // (one-time; no-op when there's nothing to migrate).
        AppPaths.MigrateLegacyFiles();

        // Clear .old-* debris from a swap that was interrupted between its two renames
        // (crash/power loss mid-update). The live folder is running us, so it's intact.
        UpdateService.SweepStaleUpdateArtifacts();

        var settings = new SettingsStore();
        var rootWindow = new MainWindow();
        _window = rootWindow;

        var store = new AccountStore(notifier: new AccountNotifier(rootWindow));
        var fastFlags = new FastFlagStore();

        var services = new AppServices(settings, store, fastFlags, rootWindow);
        rootWindow.Services = services;

        rootWindow.NavigateHome();
        _window.Activate();
        rootWindow.ApplyBackdrop(settings);

        // Cancel any in-flight background Default-client update when the window closes,
        // so no install is left half-written.
        _window.Closed += (_, _) => services.ClientUpdater.Dispose();

        // Password-locked files prompt for the password before accounts load.
        await services.UnlockAccountsAsync();
    }

    /// <summary>
    /// "Uninstall" mode (invoked by the installer's Settings → Apps entry): remove the Start
    /// Menu shortcut and the uninstall registry key, then schedule deletion of the install
    /// folder after this process exits (a running exe can't be removed). Account data in
    /// AppData is deliberately left intact.
    /// </summary>
    private static void UninstallSelf()
    {
        const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Roblox Account Manager";

        foreach (string shortcut in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "Roblox Account Manager.lnk"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Roblox Account Manager.lnk"),
                 })
        {
            try { if (File.Exists(shortcut)) File.Delete(shortcut); } catch { /* best effort */ }
        }

        try { Registry.LocalMachine.DeleteSubKeyTree(uninstallKey, throwOnMissingSubKey: false); } catch { /* best effort */ }

        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        // Delete the folder after this process exits (a running exe can't be removed). The
        // helper must NOT inherit the install folder as its working directory — a process
        // whose CWD is the folder holds it open and the rmdir would never succeed.
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c ping -n 5 127.0.0.1 >nul & rmdir /s /q \"{dir.Replace("\"", "\"\"")}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        });
    }
}
