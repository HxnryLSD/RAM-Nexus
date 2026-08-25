using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RAM.Dialogs;

/// <summary>
/// Password prompt shown when the account file is password-locked. Pass a
/// <paramref name="validate"/> delegate to verify the password and keep the dialog
/// open (with an inline error) on a wrong guess; without one, the caller checks
/// <see cref="Password"/> after <see cref="ContentDialog.ShowAsync"/> returns.
/// </summary>
public sealed partial class UnlockDialog : ContentDialog
{
    private readonly Func<string, bool>? _validate;

    public UnlockDialog(Func<string, bool>? validate = null, string? title = null, string? primaryButtonText = null)
    {
        this.InitializeComponent();
        _validate = validate;
        if (title is not null) Title = title;
        if (primaryButtonText is not null) PrimaryButtonText = primaryButtonText;
        PrimaryButtonClick += OnPrimaryButtonClick;
    }

    public string? Password => PasswordBox.Password;

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string password = PasswordBox.Password;

        if (string.IsNullOrEmpty(password))
        {
            ShowError("Please enter your password.");
            args.Cancel = true;
            return;
        }

        if (_validate is not null && !_validate(password))
        {
            ShowError("Incorrect password — please try again.");
            PasswordBox.Password = string.Empty;
            args.Cancel = true;
            return;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
