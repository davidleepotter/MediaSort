using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MediaSort.Services;

/// <summary>
/// Asks Windows DWM to render the window title bar in dark mode (Windows 10 1809+).
/// Without this, WPF windows always get a white titlebar regardless of the app theme.
/// </summary>
public static class WindowChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Windows 10 1809-1909
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Windows 10 2004+

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public static void ApplyDarkTitleBar(Window window, bool dark)
    {
        if (window == null) return;

        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDark = dark ? 1 : 0;
            // Try the modern attribute, fall back to the legacy one
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
        }

        if (window.IsLoaded)
        {
            Apply();
        }
        else
        {
            window.SourceInitialized += (_, _) => Apply();
        }
    }

    public static void ApplyCurrentTheme(Window window)
    {
        ApplyDarkTitleBar(window, ThemeManager.Current == AppTheme.Dark);
    }
}
