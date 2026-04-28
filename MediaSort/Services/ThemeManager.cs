using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace MediaSort.Services;

public enum AppTheme
{
    Light,
    Dark
}

/// <summary>
/// Reads the current Windows theme (light/dark) and exposes a set of WPF resource
/// keys whose values change with it. Watches the registry for live changes.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    public static AppTheme Current { get; private set; } = AppTheme.Light;
    public static event EventHandler? ThemeChanged;

    public static AppTheme DetectSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var value = key?.GetValue(AppsUseLightTheme);
            if (value is int i)
                return i == 0 ? AppTheme.Dark : AppTheme.Light;
        }
        catch { }
        return AppTheme.Light;
    }

    public static void Initialize()
    {
        Current = DetectSystemTheme();
        ApplyTheme(Current);

        // Listen for theme changes via system events
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General ||
            e.Category == UserPreferenceCategory.VisualStyle ||
            e.Category == UserPreferenceCategory.Color)
        {
            var detected = DetectSystemTheme();
            if (detected != Current)
            {
                Current = detected;
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    ApplyTheme(Current);
                    ThemeChanged?.Invoke(null, EventArgs.Empty);
                });
            }
        }
    }

    public static void ApplyTheme(AppTheme theme)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;

        var r = app.Resources;
        if (theme == AppTheme.Dark)
        {
            r["WindowBackground"]   = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));
            r["PanelBackground"]    = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
            r["PanelHeader"]        = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
            r["PanelForeground"]    = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
            r["MutedForeground"]    = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            r["BorderBrushColor"]   = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            r["AccentBrush"]        = new SolidColorBrush(Color.FromRgb(0x2D, 0x7B, 0xD4));
            r["AccentForeground"]   = new SolidColorBrush(Colors.White);
            r["ButtonBackground"]   = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            r["ButtonForeground"]   = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE));
            r["ButtonBorder"]       = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
            r["InputBackground"]    = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            r["ItemBackground"]     = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
            r["PreviewBackground"]  = new SolidColorBrush(Color.FromRgb(0x0F, 0x0F, 0x0F));
            r["SplitterBrush"]      = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A));
            r["EmptyForeground"]    = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        }
        else
        {
            r["WindowBackground"]   = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
            r["PanelBackground"]    = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA));
            r["PanelHeader"]        = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC));
            r["PanelForeground"]    = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            r["MutedForeground"]    = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            r["BorderBrushColor"]   = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            r["AccentBrush"]        = new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0));
            r["AccentForeground"]   = new SolidColorBrush(Colors.White);
            r["ButtonBackground"]   = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            r["ButtonForeground"]   = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            r["ButtonBorder"]       = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));
            r["InputBackground"]    = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
            r["ItemBackground"]     = new SolidColorBrush(Color.FromRgb(0xF1, 0xF1, 0xF1));
            r["PreviewBackground"]  = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
            r["SplitterBrush"]      = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD0));
            r["EmptyForeground"]    = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        }
    }
}
