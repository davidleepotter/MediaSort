using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaSort.Models;
using MediaSort.Services;
using Color = System.Windows.Media.Color;

namespace MediaSort.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings, ObservableCollection<DestinationButton> destinations)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
        _settings = settings;

        SourceFolderBox.Text = settings.SourceFolder;
        RecursiveCheck.IsChecked = settings.RecursiveScan;
        IncludeHiddenCheck.IsChecked = settings.IncludeHiddenFiles;
        RefreshThumbCacheStats();
        AutoAdvanceCheck.IsChecked = settings.AutoAdvanceAfterMove;
        ViewModeCombo.SelectedIndex = (int)settings.ViewMode;
        ConflictPolicyCombo.SelectedIndex = (int)settings.ConflictPolicy;
        ThemeCombo.SelectedIndex = (int)settings.ThemeOverride;
        AccentBox.Text = settings.AccentColor;
        AnimationSlider.Value = Math.Max(60, Math.Min(1200, settings.AnimationDurationMs));
        ThumbSizeSlider.Value = Math.Max(60, Math.Min(240, settings.ThumbnailSize));
        DuplicateThresholdSlider.Value = Math.Max(0, Math.Min(16, settings.DuplicateThreshold));
        UpdateAnimationText();
        UpdateThumbSizeText();
        UpdateDuplicateThresholdText();
        UpdateAccentSwatch();
        InitDestFontControls();
        DestinationsList.ItemsSource = destinations;
    }

    /// <summary>
    /// Populate the four font-family combos with installed fonts and seed each
    /// row's family + size from current settings.
    /// </summary>
    private void InitDestFontControls()
    {
        // Build a sorted, de-duplicated list of installed font family display names.
        // Using en-US for predictable ordering; user-typed values override on save.
        List<string> familyNames;
        try
        {
            var en = CultureInfo.GetCultureInfo("en-US");
            familyNames = Fonts.SystemFontFamilies
                .Select(ff =>
                {
                    if (ff.FamilyNames.TryGetValue(System.Windows.Markup.XmlLanguage.GetLanguage("en-US"), out var n))
                        return n;
                    return ff.Source;
                })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            familyNames = new List<string> { "Segoe UI", "Arial", "Consolas", "Tahoma", "Verdana" };
        }

        foreach (var combo in new[] { DestNameFontCombo, DestKeyFontCombo, DestPathFontCombo, DestBadgeFontCombo })
        {
            combo.ItemsSource = familyNames;
        }

        // Empty means "use system default" — show "Segoe UI" so the field isn't
        // blank and the user sees the actual font being used.
        DestNameFontCombo.Text  = DisplayFontName(_settings.DestNameFontFamily);
        DestKeyFontCombo.Text   = DisplayFontName(_settings.DestKeyFontFamily);
        DestPathFontCombo.Text  = DisplayFontName(_settings.DestPathFontFamily);
        DestBadgeFontCombo.Text = DisplayFontName(_settings.DestBadgeFontFamily);

        DestNameSizeBox.Text  = FormatSize(_settings.DestNameFontSize,  12);
        DestKeySizeBox.Text   = FormatSize(_settings.DestKeyFontSize,   10);
        DestPathSizeBox.Text  = FormatSize(_settings.DestPathFontSize,  10);
        DestBadgeSizeBox.Text = FormatSize(_settings.DestBadgeFontSize, 10);

        // Destination button overall size preset (Compact / Normal / Large).
        // Items are 0=Compact, 1=Normal, 2=Large — matches DestButtonSizeMode order.
        DestButtonSizeCombo.SelectedIndex = (int)_settings.DestButtonSize;
    }

    private static string DisplayFontName(string? stored)
    {
        var s = (stored ?? "").Trim();
        return string.IsNullOrEmpty(s) ? "Segoe UI" : s;
    }

    private static string FormatSize(double value, double fallback)
    {
        var v = (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) ? fallback : value;
        // Show as integer when whole, otherwise one decimal.
        return Math.Abs(v - Math.Round(v)) < 0.01
            ? ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static double ParseSize(string text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            || double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v))
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0) return fallback;
            return Math.Max(6.0, Math.Min(48.0, v));
        }
        return fallback;
    }

    private void UpdateAnimationText()
    {
        AnimationValueText.Text = $"{(int)AnimationSlider.Value}ms";
    }

    private void UpdateThumbSizeText()
    {
        ThumbSizeValueText.Text = $"{(int)ThumbSizeSlider.Value}px";
    }

    private void AnimationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AnimationValueText != null) UpdateAnimationText();
    }

    private void ThumbSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThumbSizeValueText != null) UpdateThumbSizeText();
    }

    private void UpdateDuplicateThresholdText()
    {
        // Show the numeric value plus a friendly word so users understand the
        // direction without having to read the help text.
        int v = (int)DuplicateThresholdSlider.Value;
        string label = v <= 1 ? "strict"
                     : v <= 4 ? "normal"
                     : v <= 8 ? "loose"
                              : "very loose";
        DuplicateThresholdText.Text = $"{v}  ({label})";
    }

    private void DuplicateThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DuplicateThresholdText != null) UpdateDuplicateThresholdText();
    }

    private void AccentBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateAccentSwatch();

    private void UpdateAccentSwatch()
    {
        if (AccentSwatch == null) return;
        try
        {
            var c = (Color)System.Windows.Media.ColorConverter.ConvertFromString(AccentBox.Text);
            AccentSwatch.Background = new SolidColorBrush(c);
        }
        catch
        {
            AccentSwatch.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose the source folder of media files"
        };
        if (!string.IsNullOrWhiteSpace(SourceFolderBox.Text) && System.IO.Directory.Exists(SourceFolderBox.Text))
            dlg.SelectedPath = SourceFolderBox.Text;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SourceFolderBox.Text = dlg.SelectedPath;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.SourceFolder = SourceFolderBox.Text.Trim();
        _settings.RecursiveScan = RecursiveCheck.IsChecked == true;
        _settings.IncludeHiddenFiles = IncludeHiddenCheck.IsChecked == true;
        _settings.AutoAdvanceAfterMove = AutoAdvanceCheck.IsChecked == true;
        _settings.ViewMode = (ViewMode)ViewModeCombo.SelectedIndex;
        _settings.ConflictPolicy = (ConflictPolicySetting)ConflictPolicyCombo.SelectedIndex;
        _settings.ThemeOverride = (ThemeOverride)ThemeCombo.SelectedIndex;
        _settings.AccentColor = AccentBox.Text.Trim();
        _settings.AnimationDurationMs = (int)AnimationSlider.Value;
        _settings.ThumbnailSize = (int)ThumbSizeSlider.Value;
        _settings.DuplicateThreshold = (int)DuplicateThresholdSlider.Value;

        // Destination button text styling (per-line family + size).
        _settings.DestNameFontFamily  = (DestNameFontCombo.Text  ?? "").Trim();
        _settings.DestKeyFontFamily   = (DestKeyFontCombo.Text   ?? "").Trim();
        _settings.DestPathFontFamily  = (DestPathFontCombo.Text  ?? "").Trim();
        _settings.DestBadgeFontFamily = (DestBadgeFontCombo.Text ?? "").Trim();
        _settings.DestNameFontSize  = ParseSize(DestNameSizeBox.Text,  12);
        _settings.DestKeyFontSize   = ParseSize(DestKeySizeBox.Text,   10);
        _settings.DestPathFontSize  = ParseSize(DestPathSizeBox.Text,  10);
        _settings.DestBadgeFontSize = ParseSize(DestBadgeSizeBox.Text, 10);

        // Destination button size preset.
        var sizeIdx = DestButtonSizeCombo.SelectedIndex;
        if (sizeIdx < 0 || sizeIdx > 2) sizeIdx = (int)DestButtonSizeMode.Normal;
        _settings.DestButtonSize = (DestButtonSizeMode)sizeIdx;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SaveDebugLog_Click(object sender, RoutedEventArgs e)
    {
        // Delegate to MainWindow which holds the live state (items, destinations, etc.)
        if (Owner is MainWindow main)
        {
            main.SaveDebugLog();
        }
        else
        {
            System.Windows.MessageBox.Show(this,
                "Could not save debug log: main window unavailable.",
                "Save Debug Log",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void RefreshThumbCacheStats()
    {
        try
        {
            var (dBytes, dCount) = MediaSort.Services.ThumbnailCache.GetDiskStats();
            var (mBytes, mCount) = MediaSort.Services.ThumbnailCache.GetMemoryStats();
            double dMb = dBytes / (1024.0 * 1024.0);
            double mMb = mBytes / (1024.0 * 1024.0);
            double diskCapMb = MediaSort.Services.ThumbnailCache.MaxDiskBytes / (1024.0 * 1024.0);
            double memCapMb  = MediaSort.Services.ThumbnailCache.MaxMemoryBytes / (1024.0 * 1024.0);
            ThumbCacheStatusText.Text = (dCount == 0 && mCount == 0)
                ? "Thumbnail cache is empty."
                : $"Memory: {mCount} ({mMb:0.0} / {memCapMb:0} MB)   Disk: {dCount} ({dMb:0.0} / {diskCapMb:0} MB)";
        }
        catch
        {
            ThumbCacheStatusText.Text = "";
        }
    }

    private void ClearThumbCache_Click(object sender, RoutedEventArgs e)
    {
        var res = System.Windows.MessageBox.Show(this,
            "This will delete all cached thumbnails (memory + disk). Thumbnails will be regenerated next time you open a folder. Continue?",
            "Clear Thumbnail Cache",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (res != System.Windows.MessageBoxResult.OK) return;
        MediaSort.Services.ThumbnailCache.ClearAll();
        RefreshThumbCacheStats();
    }
}
