using System;
using System.Threading;
using System.Windows;

namespace Soundstage.Shell;

// Fully qualified: referencing WinForms (for the tray icon) puts a second Application in scope.
public partial class App : System.Windows.Application
{
    /// <summary>
    /// Guards against a second copy of Soundstage running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two instances is not merely untidy — both publish settings to the same shared block that the
    /// audio plugin reads, so they overwrite each other and the controls appear to do nothing or to
    /// change on their own. It is also easy to end up with two: the app keeps running in the tray
    /// when its window is closed, so launching it again from the Start menu looks like starting it
    /// fresh.
    /// </para>
    /// <para>
    /// Local\ scope is deliberate: the guard is per user session, so a second signed-in user gets
    /// their own instance rather than being silently refused.
    /// </para>
    /// </remarks>
    private Mutex? _instanceLock;
    private EventWaitHandle? _showRequest;
    private Thread? _showListener;

    private const string LockName = @"Local\Soundstage-SingleInstance";
    private const string ShowName = @"Local\Soundstage-ShowWindow";

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceLock = new Mutex(initiallyOwned: true, LockName, out bool isFirst);

        if (!isFirst)
        {
            // Someone is already running. Rather than complain, do what the user actually meant by
            // launching it again: bring the existing window to the front, then quietly step aside.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowName, out EventWaitHandle? existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch
            {
                // The other instance may be shutting down. Nothing useful to do about it.
            }

            Shutdown();
            return;
        }

        // We are the one true instance. Listen for later launches asking us to surface.
        _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowName);
        _showListener = new Thread(ShowLoop) { IsBackground = true, Name = "Soundstage show-request" };
        _showListener.Start();

        base.OnStartup(e);
    }

    private void ShowLoop()
    {
        EventWaitHandle? handle = _showRequest;
        if (handle is null)
        {
            return;
        }

        while (true)
        {
            try
            {
                if (!handle.WaitOne())
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;   // shutting down
            }

            Dispatcher.Invoke(() =>
            {
                Window? w = MainWindow;
                if (w is null)
                {
                    return;
                }

                if (w.WindowState == WindowState.Minimized)
                {
                    w.WindowState = WindowState.Normal;
                }

                w.Show();
                w.Activate();
                w.Topmost = true;    // a moment on top is what actually raises it past the foreground lock
                w.Topmost = false;
            });
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRequest?.Dispose();
        try { _instanceLock?.ReleaseMutex(); } catch (ApplicationException) { /* never owned it */ }
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
