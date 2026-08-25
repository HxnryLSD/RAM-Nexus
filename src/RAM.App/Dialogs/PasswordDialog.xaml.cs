using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RAM.Dialogs;

public enum PasswordDialogMode
{
    /// <summary>Set a password on a file that isn't locked yet (new + confirm only).</summary>
    Set,
    /// <summary>Change the password of a locked file (current + new + confirm).</summary>
    Change,
    /// <summary>Remove the password, returning to DPAPI (current only).</summary>
    Remove
}

/// <summary>
/// Set / change / remove the master password of the account file. Validation lives here
/// (length, confirmation, and — when a <paramref name="verifyCurrent"/> delegate is given —
/// the current password), so a failed attempt keeps the dialog open with an inline error.
/// </summary>
public sealed partial class PasswordDialog : ContentDialog
{
    private readonly PasswordDialogMode _mode;
    private readonly Func<string, bool>? _verifyCurrent;

    public PasswordDialog(PasswordDialogMode mode, Func<string, bool>? verifyCurrent = null,
        string? title = null, string? primaryButtonText = null)
    {
        this.InitializeComponent();
        _mode = mode;
        _verifyCurrent = verifyCurrent;

        bool removing = mode == PasswordDialogMode.Remove;
        Title = title ?? mode switch
        {
            PasswordDialogMode.Change => "Change password",
            PasswordDialogMode.Remove => "Remove password",
            _ => "Set password"
        };
        PrimaryButtonText = primaryButtonText ?? mode switch
        {
            PasswordDialogMode.Change => "Change password",
            PasswordDialogMode.Remove => "Remove password",
            _ => "Set password"
        };

        CurrentBox.Visibility = removing || mode == PasswordDialogMode.Set ? Visibility.Collapsed : Visibility.Visible;
        NewBox.Visibility = removing ? Visibility.Collapsed : Visibility.Visible;
        ConfirmBox.Visibility = removing ? Visibility.Collapsed : Visibility.Visible;
        HintText.Visibility = removing ? Visibility.Collapsed : Visibility.Visible;

        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    /// <summary>New password (set/change modes only).</summary>
    public string? NewPassword => NewBox.Password;

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_mode != PasswordDialogMode.Set)
        {
            string current = CurrentBox.Password;
            if (string.IsNullOrEmpty(current))
            {
                ShowError("Enter your current password.");
                args.Cancel = true;
                return;
            }
            if (_verifyCurrent is not null && !_verifyCurrent(current))
            {
                ShowError("Incorrect current password.");
                CurrentBox.Password = string.Empty;
                args.Cancel = true;
                return;
            }
        }

        if (_mode != PasswordDialogMode.Remove)
        {
            string password = NewBox.Password;
            if (password.Length < 4)
            {
                ShowError("Password must contain at least 4 characters.");
                args.Cancel = true;
                return;
            }
            if (password != ConfirmBox.Password)
            {
                ShowError("Passwords do not match.");
                args.Cancel = true;
                return;
            }
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
