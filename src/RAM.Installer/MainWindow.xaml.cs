using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using Microsoft.Win32;

namespace RAM.Installer;

public partial class MainWindow : Window
{
    private bool _portable;
    private bool _done;
    private string _installDir = "";

    public MainWindow(bool portable, string? dir, bool elevated)
    {
        InitializeComponent();

        // Windows 11: rounded corners + dark title bar for a native, modern look.
        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        };

        SelectMode(portable);
        _installDir = dir ?? (portable
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Roblox Account Manager")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox Account Manager"));
        PathBox.Text = _installDir;

        // An elevated machine-install restart jumps straight into the extraction.
        if (elevated && !portable)
            _ = StartInstallAsync();
    }

    // ---- mode selection ----

    private void MachineCard_Click(object sender, MouseButtonEventArgs e) => SelectMode(portable: false);
    private void PortableCard_Click(object sender, MouseButtonEventArgs e) => SelectMode(portable: true);

    private void SelectMode(bool portable)
    {
        _portable = portable;
        MachineCard.Tag = portable ? null : "Selected";
        PortableCard.Tag = portable ? "Selected" : null;
        PrimaryButton.Content = portable ? "Extract" : "Install";
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Multiselect = false, InitialDirectory = PathBox.Text };
        if (dialog.ShowDialog(this) == true)
            PathBox.Text = dialog.FolderName;
    }

    // ---- actions ----

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        if (_done)
        {
            if (_portable)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_installDir}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(Path.Combine(_installDir, "Roblox Account Manager.exe")) { UseShellExecute = true });
            return;
        }

        _installDir = PathBox.Text.Trim();
        if (string.IsNullOrEmpty(_installDir))
        {
            Status("Choose an install folder first.", isError: true);
            return;
        }

        // Machine installs write to Program Files / HKLM / the public Start Menu → need
        // elevation. Restart once through the UAC prompt and continue in the new instance.
        if (!_portable && !InstallerCore.IsElevated())
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--elevated --dir \"{_installDir}\"",
            };
            try
            {
                Process.Start(psi);
                Close();
            }
            catch (Win32Exception)
            {
                Status("Installing for all users needs administrator approval — the approval was declined.", isError: true);
            }
            return;
        }

        await StartInstallAsync();
    }

    private Task StartInstallAsync()
    {
        ChoosePanel.Visibility = Visibility.Collapsed;
        WorkingPanel.Visibility = Visibility.Visible;
        SecondaryButton.Visibility = Visibility.Collapsed;
        PrimaryButton.IsEnabled = false;
        Status("", isError: false);

        var progress = new Progress<double>(p =>
        {
            Progress.Value = p * 100;
            ProgressText.Text = $"{(int)(p * 100)}% — extracting files (no internet needed)…";
        });

        return Task.Run(() => InstallerCore.Install(_portable, _installDir, progress)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                if (t.IsFaulted)
                {
                    Progress.Visibility = Visibility.Collapsed;
                    ProgressText.Text = t.Exception?.GetBaseException().Message ?? "Installation failed.";
                    Status("Installation failed.", isError: true);
                    PrimaryButton.Content = "Try again";
                    PrimaryButton.IsEnabled = true;
                    return;
                }

                _done = true;
                WorkingPanel.Visibility = Visibility.Collapsed;
                DonePanel.Visibility = Visibility.Visible;
                SecondaryButton.Visibility = Visibility.Visible;
                SecondaryButton.Content = "Close";
                PrimaryButton.IsEnabled = true;

                if (_portable)
                {
                    DoneTitle.Text = "Portable copy ready";
                    DoneDetail.Text = $"Files were extracted to:\n{_installDir}\n\nRun \"Roblox Account Manager.exe\" from that folder — no system changes were made.";
                    PrimaryButton.Content = "Open folder";
                }
                else
                {
                    DoneTitle.Text = "Installation complete";
                    DoneDetail.Text = $"Roblox Account Manager {t.Result} was installed to:\n{_installDir}\n\nA Start Menu shortcut was added, and the app can be uninstalled from Settings → Apps.";
                    PrimaryButton.Content = "Launch";
                }
            });
        }, TaskScheduler.Default);
    }

    private void Secondary_Click(object sender, RoutedEventArgs e) => Close();

    private void Status(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Foreground = new SolidColorBrush(isError
            ? Color.FromRgb(0xFF, 0x7B, 0x72)
            : Color.FromRgb(0x9A, 0xA0, 0xA6));
    }

    // DWM attributes for the modern window chrome (no-ops on Windows 10).
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
