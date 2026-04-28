using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MediaSort.Services;

/// <summary>
/// Asks Windows DWM to render the window title bar in dark mode.
/// Works on Windows 10 1809+ and Windows 11.
/// </summary>
public static class WindowChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19; // Win10 1809..1909
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;     // Win10 2004+ / Win11

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    public static void ApplyDarkTitleBar(Window window, bool dark)
    {
        if (window == null) return;

        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDark = dark ? 1 : 0;

            // Try modern attribute first (Win10 2004+ / Win11)
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            if (hr != 0)
            {
                // Fall back to legacy (Win10 1809..1909)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
            }

            // Force the non-client area to repaint with the new color
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        // SourceInitialized is the earliest moment the HWND exists.
        // Subscribing here guarantees we run before the window is shown.
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
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
