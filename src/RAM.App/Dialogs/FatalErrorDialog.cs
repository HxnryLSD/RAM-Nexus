using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace RAM.Dialogs;

/// <summary>
/// The polite "something went wrong" dialog shown instead of a silent exit: a friendly
/// message, an expandable technical-details section, and a one-click copy of the crash
/// stack. Falls back to a Win32 MessageBox when no window is available yet (startup
/// crashes before the window exists) or the UI is too broken to host a dialog.
/// </summary>
public static class FatalErrorDialog
{
    /// <summary>Compact, copy-friendly crash details: type, message, full stack.</summary>
    public static string Format(Exception? ex)
    {
        if (ex is null)
            return "(no exception details)";

        var sb = new StringBuilder();
        sb.AppendLine(ex.GetType().FullName);
        sb.AppendLine(ex.Message);
        sb.AppendLine();
        sb.AppendLine(ex.ToString());
        return sb.ToString();
    }

    /// <summary>
    /// Show the dialog (awaitable — caller keeps the app alive meanwhile). Returns true
    /// when the user actually saw a dialog, false when nothing could be shown.
    /// </summary>
    public static async Task<bool> ShowAsync(XamlRoot? xamlRoot, Exception? ex)
    {
        string details = Format(ex);
        if (xamlRoot is null)
            return ShowWin32MessageBox(details);

        try
        {
            var dialog = BuildDialog(details);
            dialog.XamlRoot = xamlRoot;
            await dialog.ShowAsync();
            return true;
        }
        catch
        {
            // The crash may have broken the UI badly enough that a dialog can't host —
            // fall back to a plain message box rather than crashing again.
            return ShowWin32MessageBox(details);
        }
    }

    /// <summary>
    /// Blocking variant for the process-terminating case (AppDomain.UnhandledException):
    /// show the dialog synchronously so the user sees it before the process exits.
    /// </summary>
    public static bool ShowBlocking(Window? window, Exception? ex)
    {
        string details = Format(ex);
        var xamlRoot = window?.Content.XamlRoot;

        // ContentDialog must run on the UI thread with a window; otherwise MessageBox.
        if (xamlRoot is null || window?.DispatcherQueue?.HasThreadAccess != true)
            return ShowWin32MessageBox(details);

        try
        {
            var dialog = BuildDialog(details);
            dialog.XamlRoot = xamlRoot;
            dialog.ShowAsync().GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return ShowWin32MessageBox(details);
        }
    }

    private static ContentDialog BuildDialog(string details)
    {
        var copyButton = new Button { Content = "Copy details" };
        copyButton.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(details);
            Clipboard.SetContent(package);
            copyButton.Content = "Copied ✓";
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "Roblox Account Manager hit an unexpected problem. Your accounts are safe — " +
                   $"the error has been logged to {CrashLog.FilePath}.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(copyButton);
        panel.Children.Add(new Expander
        {
            Header = "Technical details",
            IsExpanded = false,
            Content = new ScrollViewer
            {
                MaxHeight = 260,
                Content = new TextBlock
                {
                    Text = details,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            }
        });

        return new ContentDialog
        {
            Title = "Unexpected error",
            Content = panel,
            PrimaryButtonText = "OK",
            DefaultButton = ContentDialogButton.Primary
        };
    }

    /// <summary>Last-resort dialog when no XamlRoot is available. Text isn't copyable, but crash.log has it.</summary>
    private static bool ShowWin32MessageBox(string details)
    {
        string message =
            "Roblox Account Manager hit an unexpected problem.\r\n\r\n" +
            "{0}\r\n\r\n" +
            $"The full error has been logged to {CrashLog.FilePath}.";
        MessageBoxW(IntPtr.Zero, string.Format(message, details),
            "Roblox Account Manager", 0x10 /* MB_ICONERROR */ | 0x1000 /* MB_SYSTEMMODAL */);
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
