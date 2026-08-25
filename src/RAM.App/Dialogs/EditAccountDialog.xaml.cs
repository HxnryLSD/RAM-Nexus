using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RAM.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace RAM.Dialogs;

/// <summary>
/// Bitwarden-style vault editor for one account: alias, group, notes, a stored password
/// (with a generator), and a copy-cookie action. Edits are applied by the caller after the
/// dialog closes — the dialog only collects the values.
/// </summary>
public sealed partial class EditAccountDialog : ContentDialog
{
    private readonly Account _account;

    public EditAccountDialog(Account account)
    {
        this.InitializeComponent();
        _account = account;

        HeaderText.Text = string.IsNullOrEmpty(account.Username)
            ? "Account"
            : $"{account.Username}{(account.UserID > 0 ? $" · ID {account.UserID}" : "")}";

        AliasBox.Text = account.Alias ?? "";
        GroupBox.Text = account.Group;
        NotesBox.Text = account.Description ?? "";
        PasswordBox.Password = account.Password ?? "";
    }

    /// <summary>New alias (null when cleared).</summary>
    public string? Alias => string.IsNullOrWhiteSpace(AliasBox.Text) ? null : AliasBox.Text.Trim();

    /// <summary>New group (defaults back to "Default" when cleared).</summary>
    public string Group => string.IsNullOrWhiteSpace(GroupBox.Text) ? "Default" : GroupBox.Text.Trim();

    /// <summary>New notes (null when cleared).</summary>
    public string? Notes => string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim();

    /// <summary>New stored password (null when cleared).</summary>
    public string? StoredPassword => string.IsNullOrEmpty(PasswordBox.Password) ? null : PasswordBox.Password;

    private void Generate_Click(object sender, RoutedEventArgs e)
        => PasswordBox.Password = GeneratePassword(16);

    /// <summary>Cryptographically random password using a broad alphabet.</summary>
    private static string GeneratePassword(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%^&*-_=+";
        byte[] bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }

    private void CopyCookie_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_account.SecurityToken))
        {
            CopyCookieStatus.Text = "No cookie to copy.";
            return;
        }

        var package = new DataPackage();
        package.SetText(_account.SecurityToken);
        Clipboard.SetContent(package);
        CopyCookieStatus.Text = "Cookie copied to clipboard.";
    }
}
