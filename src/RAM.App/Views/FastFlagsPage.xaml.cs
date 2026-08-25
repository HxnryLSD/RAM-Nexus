using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using RAM.Core.Roblox;
using RAM.Core.Roblox.FastFlags;
using RAM.Core.Roblox.Rdd;

namespace RAM.Views;

public sealed partial class FastFlagsPage : Page
{
    private AppServices? _services;

    public FastFlagsPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = (AppServices)e.Parameter;
        BuildSections();
        RefreshTargets();
        _loading = false; // from here on, picker changes are user intent
        ApplyToInstall();
    }

    /// <summary>True while the picker is being populated programmatically (not user intent).</summary>
    private bool _loading = true;

    /// <summary>
    /// Populates the target-install picker with the installed RDD deployments and selects
    /// the active install (the same deployment Accounts-page Launch uses — see
    /// <see cref="RddActiveInstall"/>). The picker doubles as the active-install selector:
    /// changing it re-targets the whole app, not just this page.
    /// </summary>
    private void RefreshTargets()
    {
        TargetBox.Items.Clear();

        var store = new RddDeploymentStore(_services!.Settings.Get(RddOptions.SettingsKey, RddOptions.DefaultRoot));
        var installs = store.ListInstalls().ToList();

        foreach (var folder in installs)
        {
            var tag = store.GetTag(folder) ?? "(untagged)";
            TargetBox.Items.Add(new ComboBoxItem { Content = $"{tag} — {new DirectoryInfo(folder).Name}", Tag = folder });
        }

        string? active = RddActiveInstall.Resolve(_services!.Settings, store);
        TargetBox.SelectedIndex = active is null ? -1 : installs.FindIndex(f =>
            string.Equals(f, active, StringComparison.OrdinalIgnoreCase));
    }

    private string? SelectedInstallFolder()
        => (TargetBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyToInstall();
        if (_loading) return;
        if (SelectedInstallFolder() is string folder)
            RddActiveInstall.Set(_services!.Settings, folder);
    }

    private void BuildSections()
    {
        foreach (var category in FastFlagCatalog.Categories)
        {
            RootPanel.Children.Add(new TextBlock
            {
                Text = category,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                Margin = new Thickness(0, 12, 0, 2)
            });

            foreach (var flag in FastFlagCatalog.InCategory(category))
                RootPanel.Children.Add(BuildFlagRow(flag));
        }
    }

    private UIElement BuildFlagRow(FastFlagDef flag)
    {
        var enabled = new ToggleSwitch
        {
            IsOn = _services!.FastFlags.IsActivated(flag.Name),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 56,
            Margin = new Thickness(0, 0, 8, 0)
        };
        ToolTipService.SetToolTip(enabled, "Turn this fast flag on to apply its value, or off to use Roblox's default.");

        var nameBlock = new TextBlock
        {
            Text = flag.Name,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var descBlock = new TextBlock
        {
            Text = flag.Description,
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };
        ToolTipService.SetToolTip(nameBlock, flag.Description);

        var textColumn = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textColumn.Children.Add(nameBlock);
        textColumn.Children.Add(descBlock);

        var row = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        row.Children.Add(enabled);
        Grid.SetColumn(enabled, 0);
        row.Children.Add(textColumn);
        Grid.SetColumn(textColumn, 1);

        if (flag.IsBoolean)
        {
            // A boolean flag's value is simply its on/off state.
            enabled.Toggled += (_, _) =>
            {
                if (enabled.IsOn) _services!.FastFlags.Set(flag.Name, "true");
                else _services.FastFlags.Remove(flag.Name);
                ApplyToInstall();
            };
        }
        else
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280, GridUnitType.Pixel) });

            var label = new TextBlock
            {
                Text = flag.Suggested.ToString(),
                Width = 48,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };

            var slider = new Slider
            {
                Minimum = flag.Min,
                Maximum = flag.Max,
                StepFrequency = flag.Step,
                Width = 180,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = enabled.IsOn
            };
            ToolTipService.SetToolTip(slider, flag.Description);

            if (_services!.FastFlags.Get(flag.Name) is string stored && int.TryParse(stored, out var saved))
                slider.Value = saved;
            else
                slider.Value = flag.Suggested;

            var valueColumn = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            valueColumn.Children.Add(slider);
            valueColumn.Children.Add(label);

            // Dragging a slider fires ValueChanged continuously; persisting + re-patching
            // ClientAppSettings.json on every tick would hammer the disk. Debounce: the value
            // label updates live, but the file write happens once the slider settles.
            var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            saveTimer.Tick += (_, _) =>
            {
                saveTimer.Stop();
                _services!.FastFlags.Set(flag.Name, label.Text);
                ApplyToInstall();
            };

            enabled.Toggled += (_, _) =>
            {
                saveTimer.Stop();
                if (enabled.IsOn)
                {
                    slider.IsEnabled = true;
                    _services!.FastFlags.Set(flag.Name, ((int)Math.Round(slider.Value)).ToString());
                }
                else
                {
                    slider.IsEnabled = false;
                    _services.FastFlags.Remove(flag.Name);
                }
                ApplyToInstall();
            };

            slider.ValueChanged += (_, _) =>
            {
                label.Text = ((int)Math.Round(slider.Value)).ToString();
                if (enabled.IsOn)
                {
                    saveTimer.Stop();
                    saveTimer.Start();
                }
            };

            // Persist any already-activated flag with its current value.
            if (enabled.IsOn)
                _services!.FastFlags.Set(flag.Name, label.Text);

            row.Children.Add(valueColumn);
            Grid.SetColumn(valueColumn, 2);
        }

        return row;
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
        => ApplyToInstall();

    /// <summary>
    /// Writes the activated fast flags (plus the FPS unlock, if enabled in Settings) into the
    /// ClientAppSettings.json of the selected (active) RDD install.
    /// </summary>
    private void ApplyToInstall()
    {
        try
        {
            string? install = SelectedInstallFolder();
            if (install is null)
            {
                ApplyStatus.Text = "No RDD install found. Download a deployment on the RDD page first — only RDD installs are used, never the normal Roblox installation.";
                return;
            }

            // Same apply path the background Default-client updater uses, so a freshly
            // updated install always gets exactly what this page shows.
            bool ok = ClientSettingsApplier.Apply(install, _services!.Settings, _services.FastFlags);
            bool unlockFps = _services.Settings.Get("UnlockFPS", false);
            var active = _services.FastFlags.GetActivated();

            ApplyStatus.Text = !ok
                ? "Could not write settings."
                : unlockFps || active.Count > 0
                    ? $"Applied to {install} → ClientSettings\\{ClientSettingsPatcher.SettingsFileName}"
                    : "No active fast flags — Roblox defaults restored.";
        }
        catch (UnauthorizedAccessException)
        {
            ApplyStatus.Text = "Access denied — the Roblox install is in a protected folder (Program Files). Run the app as administrator.";
        }
        catch (Exception ex)
        {
            ApplyStatus.Text = $"Error: {ex.Message}";
        }
    }
}