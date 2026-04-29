using System;
using System.Windows;
using System.Windows.Threading;
using MediaSort.Services;

namespace MediaSort;

public partial class App : System.Windows.Application
{
    private SplashScreen? _splash;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Show the splash manually so we can control when it closes — we want it visible
        // through the entire MainWindow load (theme init, control templating, first paint),
        // not just until WPF starts. autoClose:false leaves it up until we call Close().
        try
        {
            _splash = new SplashScreen("Assets/Splash.png");
            _splash.Show(autoClose: false, topMost: true);
        }
        catch
        {
            // Splash is best-effort — never let it block startup.
            _splash = null;
        }

        ThemeManager.Initialize();
        base.OnStartup(e);

        // MainWindow is created asynchronously by WPF AFTER OnStartup returns when using
        // StartupUri, so MainWindow is still null here. Defer the subscription via the
        // dispatcher at Loaded priority — by the time this lambda runs, MainWindow exists.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_splash == null) return;
            var w = MainWindow;
            if (w == null)
            {
                // Last-ditch fallback: close the splash with a short delay so we never
                // leave the user staring at it forever.
                Dispatcher.BeginInvoke(new Action(CloseSplash), DispatcherPriority.ApplicationIdle);
                return;
            }
            // ContentRendered fires after the very first paint of the window's visual tree.
            w.ContentRendered += MainWindow_ContentRendered;
            // Safety net: also close on Activated in case ContentRendered is delayed.
            w.Activated += MainWindow_Activated;
        }), DispatcherPriority.Loaded);
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (sender is Window w)
        {
            w.ContentRendered -= MainWindow_ContentRendered;
            w.Activated -= MainWindow_Activated;
        }
        CloseSplash();
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (sender is Window w)
        {
            w.Activated -= MainWindow_Activated;
        }
        // Close on next idle tick so any pending first paint has a chance to land first.
        Dispatcher.BeginInvoke(new Action(CloseSplash), DispatcherPriority.ApplicationIdle);
    }

    private void CloseSplash()
    {
        try
        {
            _splash?.Close(TimeSpan.FromMilliseconds(300));
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
