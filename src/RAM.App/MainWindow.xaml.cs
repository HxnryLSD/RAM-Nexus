using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RAM.Core.Infrastructure;
using RAM.Views;

namespace RAM;

public sealed partial class MainWindow : Window
{
    /// <summary>The app service bundle, set by the composition root before first navigation.</summary>
    public AppServices? Services { get; set; }

    public MainWindow()
    {
        this.InitializeComponent();

        // Titlebar icon from the same asset as the exe icon (silently skipped if
        // the file is missing, e.g. a bare Debug launch from the IDE output dir).
        string ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "ram.ico");
        if (System.IO.File.Exists(ico))
            this.AppWindow.SetIcon(ico);

        // Custom title bar: extend content into the caption area and make the
        // bar draggable. The system caption buttons (minimize / maximize /
        // close) remain visible at the top-right and are handled by the OS.
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(DragRegion);
    }

    /// <summary>
    /// Navigate to the initial page. Called by the composition root once
    /// <see cref="Services"/> is set — navigation needs the service bundle to pass along.
    /// </summary>
    public void NavigateHome()
    {
        ContentFrame.Navigate(typeof(AccountsPage), Services);
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    /// <summary>
    /// Applies the window material (Mica / Acrylic) from the persisted settings.
    /// Settings keys: BackdropEnabled (bool), BackdropMode (Mica|MicaAlt|Acrylic|None),
    /// BackdropTransparency (bool — frosted Acrylic vs opaque-ish fallback).
    /// </summary>
    public void ApplyBackdrop(SettingsStore settings)
    {
        bool enabled = settings.Get("BackdropEnabled", true);
        string mode = settings.Get("BackdropMode", "Mica");
        bool transparent = settings.Get("BackdropTransparency", true);

        if (!enabled || mode == "None")
        {
            SystemBackdrop = null;
            return;
        }

        SystemBackdrop = mode switch
        {
            "MicaAlt" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            // Acrylic with transparency ON → true frosted glass.
            // Acrylic with transparency OFF → Mica Alt (opaque-ish material fallback).
            "Acrylic" when transparent => new DesktopAcrylicBackdrop(),
            "Acrylic" => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            _ => new MicaBackdrop { Kind = MicaKind.Base }
        };
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not NavigationViewItem item || item.Tag is not string tag)
            return;

        var target = tag switch
        {
            "RDD" => typeof(RddPage),
            "Settings" => typeof(SettingsPage),
            "FastFlags" => typeof(FastFlagsPage),
            "About" => typeof(AboutPage),
            _ => typeof(AccountsPage)
        };

        if (ContentFrame.CurrentSourcePageType != target)
            ContentFrame.Navigate(target, Services);
    }
}
