using System;
using System.Collections.ObjectModel;
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
        UpdateAnimationText();
        UpdateThumbSizeText();
        UpdateAccentSwatch();
        DestinationsList.ItemsSource = destinations;
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
            var (bytes, count) = MediaSort.Services.ThumbnailCache.GetDiskStats();
            double mb = bytes / (1024.0 * 1024.0);
            ThumbCacheStatusText.Text = count == 0
                ? "Thumbnail cache is empty."
                : $"On disk: {count} thumbnail(s), {mb:0.0} MB.";
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
