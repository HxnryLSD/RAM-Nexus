using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RAM.Views;

/// <summary>
/// Shared "enter a place ID (optionally a job)" prompt used by the Accounts and RDD
/// pages' launch flows. Returns the chosen place + job, or null when cancelled.
/// </summary>
public static class PlaceLaunchPrompt
{
    public static async Task<(long PlaceId, string? JobId)?> AskAsync(
        XamlRoot xamlRoot, string title, string primaryButtonText, long initialPlaceId, Brush errorBrush)
    {
        var placeBox = new NumberBox
        {
            Header = "Place ID",
            Minimum = 0,
            Value = initialPlaceId,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var jobBox = new TextBox
        {
            Header = "Job ID (optional)",
            PlaceholderText = "Leave empty to join the default server"
        };
        var errorText = new TextBlock
        {
            Foreground = errorBrush,
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap
        };

        var dialog = new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 12,
                MinWidth = 340,
                Children = { placeBox, jobBox, errorText }
            },
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (double.IsNaN(placeBox.Value) || (long)placeBox.Value <= 0)
            {
                errorText.Text = "Enter a valid place ID (a positive number).";
                errorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        string job = jobBox.Text.Trim();
        return ((long)placeBox.Value, string.IsNullOrEmpty(job) ? null : job);
    }
}
