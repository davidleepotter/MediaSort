using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MediaSort.Services;

namespace MediaSort;

public partial class App : System.Windows.Application
{
    private SplashScreen? _splash;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance enforcement — must run before splash/theme init so a
        // second launch never flashes a window or competes for resources.
        // If another instance is already running, this method has already sent
        // an "activate" ping over a named pipe; we just shut down silently.
        if (!SingleInstance.TryAcquire())
        {
            // Use Environment.Exit because Shutdown()-then-return doesn't always
            // fire fast enough — base.OnStartup would still create the main window.
            Shutdown();
            Environment.Exit(0);
            return;
        }

        // Show the splash manually so we can control when it closes — we want it visible
        // through the entire MainWindow load (theme init, control templating, scan kickoff,
        // first paint), not just until WPF starts. autoClose:false leaves it up until we
        // call Close() ourselves.
        try
        {
            _splash = new SplashScreen("Assets/Splash.png");
            _splash.Show(autoClose: false, topMost: true);
        }
        catch
        {
            _splash = null;
        }

        ThemeManager.Initialize();
        base.OnStartup(e);

        // MainWindow is created asynchronously by WPF AFTER OnStartup returns when using
        // StartupUri, so MainWindow is null right now. Defer the subscription until the
        // dispatcher reaches Loaded priority — by then MainWindow exists.
        // The window itself starts at Opacity=0 (set in XAML) so the user never sees a
        // half-rendered or wrong-themed window flash. Once it has rendered its first frame,
        // we fade it in and close the splash.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var w = MainWindow;
            if (w == null)
            {
                CloseSplash();
                return;
            }
            w.ContentRendered += MainWindow_ContentRendered;
        }), DispatcherPriority.Loaded);
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (sender is not Window w) return;
        w.ContentRendered -= MainWindow_ContentRendered;

        // Wait one extra dispatcher pass at Render priority so any async layout from
        // ContentRendered handlers (initial scan UI updates, etc.) settles before we fade in.
        w.Dispatcher.BeginInvoke(new Action(() => RevealWindow(w)), DispatcherPriority.Render);
    }

    private void RevealWindow(Window w)
    {
        // Fade the main window from 0 -> 1 while the splash fades out, so the transition
        // looks deliberate rather than a hard cut.
        var fadeIn = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(220),
            FillBehavior = FillBehavior.HoldEnd,
        };
        try
        {
            w.BeginAnimation(Window.OpacityProperty, fadeIn);
        }
        catch
        {
            // If the animation can't start for any reason, just snap to visible so the
            // user is never left with an invisible window.
            w.Opacity = 1.0;
        }

        CloseSplash();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Release the single-instance mutex + pipe server so subsequent launches
        // (after this process exits) can become the new primary instance.
        try { SingleInstance.Release(); } catch { }
        base.OnExit(e);
    }

    private void CloseSplash()
    {
        try
        {
            _splash?.Close(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // ignore
        }
        finally
        {
            _splash = null;
        }
    }
}
