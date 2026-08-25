using System.IO;

namespace RAM.Installer;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Console modes used by scripts/build-installer.sh.
        if (args.Length >= 3 && args[0] == "--pack")
            return Payload.Pack(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "--trim")
            return Payload.Trim(args[1]);
        if (args.Length >= 3 && args[0] == "--extract")
        {
            using var fs = File.OpenRead(args[1]);
            string version = Payload.Extract(fs, args[2], null);
            Console.WriteLine($"Extracted payload (app {version}).");
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }
}
