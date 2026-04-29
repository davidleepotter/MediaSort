using System;
using System.Windows;
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

        // Close the splash with a fade as soon as the main window has actually rendered
        // its first frame (ContentRendered), so the user never sees a white window.
        if (_splash != null && MainWindow != null)
        {
            MainWindow.ContentRendered += MainWindow_ContentRendered;
        }
    }

    private void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (sender is Window w)
        {
            w.ContentRendered -= MainWindow_ContentRendered;
        }
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
