using System.Runtime.InteropServices;
using System.Threading;

namespace RAM;

/// <summary>
/// Single-instance guard: a named mutex claims the "one RAM at a time" slot for the whole
/// machine. A second launch signals a named event — which wakes this instance's wait
/// thread and brings its window to the front — then exits before building any services.
/// Without this, two instances race the RDD install root through per-process locks
/// (staged-install corruption) and last-writer-wins each other's AccountData.json.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\RobloxAccountManager-SingleInstance";
    private const string ActivateEventName = @"Local\RobloxAccountManager-Activate";

    private static Mutex? _mutex;
    private static Thread? _waitThread;
    private static CancellationTokenSource? _waitCts;

    /// <summary>
    /// True when this process is the first instance (and owns the guard). False when another
    /// instance is running: the event was signalled so its window comes forward, and the
    /// caller must exit immediately.
    /// </summary>
    public static bool TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName, out _);

        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance died hard (crash / Task Manager) without releasing.
            // The wait still granted ownership to us — that's exactly what we want.
            owned = true;
        }

        if (!owned)
        {
            // Another instance owns it — nudge its window to the front and bail.
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch
            {
                // The owner may be mid-exit; either way we still exit too.
            }
            mutex.Dispose();
            return false;
        }

        _mutex = mutex;
        StartActivationWatch();
        return true;
    }

    /// <summary>Watch for second-launch activations while this instance owns the guard.</summary>
    private static void StartActivationWatch()
    {
        try
        {
            var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

            _waitCts = new CancellationTokenSource();
            _waitThread = new Thread(() =>
            {
                try
                {
                    // WaitHandle can't take a CancellationToken; poll so shutdown stays prompt.
                    while (!_waitCts.IsCancellationRequested)
                    {
                        if (activate.WaitOne(500))
                            App.TryBringToForeground();
                    }
                }
                catch
                {
                    // Abandoned/failed event handling must never kill the process.
                }
            })
            {
                IsBackground = true,
                Name = "SingleInstanceActivateWatch"
            };
            _waitThread.Start();
        }
        catch
        {
            // If activation watching can't be set up, single-instance still holds —
            // second launches just won't focus the existing window.
        }
    }

    /// <summary>Release everything on shutdown.</summary>
    public static void Release()
    {
        try { _waitCts?.Cancel(); } catch { }
        // Give the watcher a beat to see the cancellation before the process tears down.
        try { _waitThread?.Join(1000); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { } // same-thread acquisition/release only
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
        _waitCts?.Dispose();
        _waitCts = null;
    }
}

public partial class App
{
    /// <summary>The live window of THIS process (null until OnLaunched builds it).</summary>
    internal static MainWindow? InstanceWindow { get; set; }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Foreground an existing window after a second launch was bounced.</summary>
    internal static void TryBringToForeground()
    {
        InstanceWindow?.DispatcherQueue?.TryEnqueue(() =>
        {
            var window = InstanceWindow;
            if (window is null) return;

            // Undo a minimized state first, or Activate() restores nothing visible.
            var presenter = window.AppWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter;
            if (presenter is not null &&
                presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            {
                presenter.Restore();
            }

            window.Activate();
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (handle != IntPtr.Zero)
                SetForegroundWindow(handle);
        });
    }
}
