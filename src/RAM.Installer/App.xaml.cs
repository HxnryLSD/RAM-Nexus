using System.Windows;

namespace RAM.Installer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        bool portable = args.Contains("--portable");
        string? dir = Value(args, "--dir");

        // Headless portable extract — used by scripts for smoke tests / silent installs.
        if (args.Contains("--silent"))
        {
            int exit = RunSilent(dir, portable);
            Shutdown(exit);
            return;
        }

        var window = new MainWindow(portable: portable, dir: dir, elevated: args.Contains("--elevated"));
        window.Show();
    }

    private static int RunSilent(string? dir, bool portable)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            Console.Error.WriteLine("--silent requires --dir <path>");
            return 2;
        }
        try
        {
            InstallerCore.Install(portable: portable, dir: dir, progress: null);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? Value(string[] args, string key)
    {
        int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
