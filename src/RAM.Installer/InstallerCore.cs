using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace RAM.Installer;

/// <summary>Machine-wide install plumbing shared by the UI and silent mode.</summary>
public static class InstallerCore
{
    private const string PayloadName = "RAM.Installer.payload.br";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Roblox Account Manager";

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static Stream OpenPayload()
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName)
            ?? throw new InvalidOperationException("Installer payload is missing — rebuild with scripts/build-installer.sh.");
    }

    /// <summary>
    /// Extract the app into <paramref name="dir"/>; for machine installs also register the
    /// uninstall entry and Start Menu shortcut. Returns the packaged app version.
    /// </summary>
    public static string Install(bool portable, string dir, IProgress<double>? progress)
    {
        Directory.CreateDirectory(dir);
        string appVersion;
        using (Stream payload = OpenPayload())
            appVersion = Payload.Extract(payload, dir, progress);

        if (!portable)
        {
            WriteUninstallRegistry(dir, appVersion);
            CreateStartMenuShortcut(dir);
        }
        return appVersion;
    }

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "Roblox Account Manager.lnk");

    private static void WriteUninstallRegistry(string dir, string version)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Roblox Account Manager");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Roblox Account Manager");
        key.SetValue("InstallLocation", dir);
        key.SetValue("DisplayIcon", Path.Combine(dir, "Roblox Account Manager.exe"));
        // The app itself carries an --uninstall mode (it's self-contained and elevated).
        string exe = Path.Combine(dir, "Roblox Account Manager.exe");
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("NoModify", 1);
        key.SetValue("NoRepair", 1);
    }

    private static void CreateStartMenuShortcut(string dir)
    {
        // WScript.Shell COM is the standard way to write .lnk files from .NET.
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic lnk = shell.CreateShortcut(ShortcutPath);
            string exe = Path.Combine(dir, "Roblox Account Manager.exe");
            lnk.TargetPath = exe;
            lnk.WorkingDirectory = dir;
            lnk.IconLocation = $"{exe},0";
            lnk.Save();
            Marshal.FinalReleaseComObject(lnk);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
