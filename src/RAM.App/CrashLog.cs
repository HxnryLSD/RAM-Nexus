using System.Text;
using RAM.Core;

namespace RAM;

/// <summary>
/// Writes unhandled exceptions to <c>crash.log</c> in the AppData data root
/// (AppPaths.CrashLog, alongside AccountData.json). Catches crashes that never reach the
/// UI (background threads, XAML binding failures, unobserved tasks) so they leave a trace
/// even when WER doesn't produce a dump. Pure file I/O, no UI, so it is safe to call from
/// any thread at any time; a failure to log is swallowed (logging must never make a crash
/// worse).
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    /// <summary>Rotate past this size so a crash loop can't fill the disk.</summary>
    private const long MaxBytes = 1_000_000; // ~1 MB

    /// <summary>crash.log in the AppData data root, like AccountStore's data file.</summary>
    public static string FilePath => AppPaths.CrashLog;

    /// <summary>
    /// Subscribes the non-fatal process-level exception source. The fatal sources are wired
    /// in <c>App</c> so they can also show the polite error dialog: XAML-thread exceptions
    /// on <see cref="Microsoft.UI.Xaml.Application.UnhandledException"/> and process-
    /// terminating ones on <see cref="AppDomain.UnhandledException"/>.
    /// </summary>
    public static void Install()
    {
        // Unobserved task exceptions never kill the process, so they are logged but not
        // surfaced to the user (showing a dialog for every stray task would be noise).
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("Unobserved task exception", e.Exception);
    }

    /// <summary>Append a timestamped entry describing <paramref name="ex"/> under <paramref name="context"/>.</summary>
    public static void Write(string context, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                var sb = new StringBuilder();
                sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).AppendLine("] RAM-Nexus");
                // Identify the exact build: community reports must be attributable to a release.
                sb.Append("  Version:  ")
                  .AppendLine(typeof(CrashLog).Assembly.GetName().Version?.ToString(3) ?? "unknown");
                sb.Append("  Context:  ").AppendLine(context);
                if (ex is null)
                {
                    sb.AppendLine("  (no exception details)");
                }
                else
                {
                    sb.AppendLine($"  Type:     {ex.GetType().FullName}");
                    sb.AppendLine($"  Message:  {ex.Message}");
                    var assembly = ex.Source
                        ?? ex.TargetSite?.DeclaringType?.Assembly?.GetName().Name
                        ?? ex.GetType().Assembly.GetName().Name;
                    sb.AppendLine($"  Assembly: {assembly}");
                    sb.AppendLine("  Stack:");
                    foreach (var line in (ex.ToString() ?? "").Split('\n'))
                        sb.AppendLine("    " + line.TrimEnd('\r'));
                }
                sb.AppendLine();

                string path = FilePath;
                string? dir = Path.GetDirectoryName(path);
                if (dir is not null)
                    Directory.CreateDirectory(dir);

                // Rotate past the cap: keep one previous copy, then start fresh.
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Copy(path, path + ".old", overwrite: true);
                    File.WriteAllText(path, string.Empty);
                }

                File.AppendAllText(path, sb.ToString());
            }
        }
        catch
        {
            // Logging must never make a crash worse.
        }
    }
}
