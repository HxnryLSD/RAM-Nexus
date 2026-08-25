using Microsoft.UI.Xaml.Controls;
using RAM.Core.Roblox;

namespace RAM;

/// <summary>
/// WinUI 3 implementation of <see cref="IAccountNotifier"/> (the shell-side half of
/// the RAM.Core prompt abstraction). RAM.Core calls these methods synchronously, but a
/// ContentDialog can only run while the UI thread awaits <see cref="ContentDialog.ShowAsync"/> —
/// blocking the UI thread would deadlock the dialog. The methods therefore fire-and-forget
/// the dialog; callers that need a yes/no answer should await a ContentDialog directly,
/// like the pages already do.
/// </summary>
public sealed class AccountNotifier : IAccountNotifier
{
    private readonly MainWindow _window;

    public AccountNotifier(MainWindow window) => _window = window;

    public void Info(string message, string? title = null) => _ = ShowAsync(message, title);
    public void Warn(string message, string? title = null) => _ = ShowAsync(message, title);
    public void Error(string message, string? title = null) => _ = ShowAsync(message, title);

    private async Task ShowAsync(string message, string? title)
    {
        var xamlRoot = _window.Content.XamlRoot;
        if (xamlRoot is null) return;

        var dialog = new ContentDialog
        {
            Title = title ?? "Roblox Account Manager",
            Content = message,
            PrimaryButtonText = "OK",
            XamlRoot = xamlRoot
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch
        {
            // Dialog host can be torn down (e.g. during app shutdown) — nothing to do.
        }
    }
}
