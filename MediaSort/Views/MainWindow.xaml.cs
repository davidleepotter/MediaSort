using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MediaSort.Models;
using MediaSort.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace MediaSort.Views;

public partial class MainWindow : Window
{
    public ObservableCollection<MediaItem> MediaItems { get; } = new();
    public ObservableCollection<DestinationButton> Destinations { get; } = new();

    private AppSettings _settings = new();
    private bool _suppressSliderUpdate;
    private bool _suppressSelectionUpdate;

    public MainWindow()
    {
        InitializeComponent();

        ListView_List.ItemsSource = MediaItems;
        ListView_Details.ItemsSource = MediaItems;
        ListView_Thumbs.ItemsSource = MediaItems;
        DestinationsPanel.ItemsSource = Destinations;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    // ----------------- LIFECYCLE -----------------

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = SettingsService.Load();

        RecursiveCheck.IsChecked = _settings.RecursiveScan;
        ViewModeCombo.SelectedIndex = (int)_settings.ViewMode;

        foreach (var d in _settings.Destinations)
            Destinations.Add(SettingsService.FromSerializable(d));

        if (!string.IsNullOrWhiteSpace(_settings.SourceFolder) && Directory.Exists(_settings.SourceFolder))
        {
            SetSourceFolder(_settings.SourceFolder);
        }

        ApplyViewMode();
        Title = $"MediaSort v{VersionInfo.GetVersion()}";
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.RecursiveScan = RecursiveCheck.IsChecked == true;
        _settings.ViewMode = (ViewMode)ViewModeCombo.SelectedIndex;
        _settings.Destinations = Destinations.Select(SettingsService.ToSerializable).ToList();
        SettingsService.Save(_settings);
    }

    // ----------------- SOURCE FOLDER -----------------

    private void PickSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose the source folder of media files"
        };
        if (!string.IsNullOrWhiteSpace(_settings.SourceFolder) && Directory.Exists(_settings.SourceFolder))
            dlg.SelectedPath = _settings.SourceFolder;

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SetSourceFolder(dlg.SelectedPath);
            _settings.SourceFolder = dlg.SelectedPath;
            SaveSettings();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_settings.SourceFolder))
            SetSourceFolder(_settings.SourceFolder);
    }

    private void Recursive_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _settings.RecursiveScan = RecursiveCheck.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(_settings.SourceFolder))
            SetSourceFolder(_settings.SourceFolder);
        SaveSettings();
    }

    private void SetSourceFolder(string folder)
    {
        SourcePathText.Text = folder;
        MediaItems.Clear();

        var items = MediaScanner.Scan(folder, RecursiveCheck.IsChecked == true).ToList();
        foreach (var i in items) MediaItems.Add(i);

        UpdatePositionDisplay();
        StatusText.Text = $"{MediaItems.Count} media file(s) found";

        if (MediaItems.Count > 0)
        {
            SelectIndex(0);
        }
        else
        {
            ClearPreview();
        }

        // background-load thumbnails
        _ = Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (item.Kind == MediaKind.Image)
                {
                    var thumb = ThumbnailLoader.LoadThumbnail(item, 128);
                    Dispatcher.Invoke(() => item.Thumbnail = thumb);
                }
            }
        });
    }

    // ----------------- VIEW MODE -----------------

    private void ViewModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyViewMode();
        SaveSettings();
    }

    private void ApplyViewMode()
    {
        ListView_List.Visibility = Visibility.Collapsed;
        ListView_Details.Visibility = Visibility.Collapsed;
        ListView_Thumbs.Visibility = Visibility.Collapsed;

        switch ((ViewMode)ViewModeCombo.SelectedIndex)
        {
            case ViewMode.List:
                ListView_List.Visibility = Visibility.Visible;
                break;
            case ViewMode.Details:
                ListView_Details.Visibility = Visibility.Visible;
                break;
            case ViewMode.Thumbnails:
                ListView_Thumbs.Visibility = Visibility.Visible;
                break;
        }

        // sync selection across the (now visible) list
        var idx = GetSelectedIndex();
        SelectIndex(idx >= 0 ? idx : 0);
    }

    private Selector? ActiveSelector =>
        (ViewMode)ViewModeCombo.SelectedIndex switch
        {
            ViewMode.List => ListView_List,
            ViewMode.Details => ListView_Details,
            ViewMode.Thumbnails => ListView_Thumbs,
            _ => null
        };

    private int GetSelectedIndex()
    {
        return ActiveSelector?.SelectedIndex ?? -1;
    }

    private void SelectIndex(int index)
    {
        if (MediaItems.Count == 0) return;
        index = Math.Max(0, Math.Min(index, MediaItems.Count - 1));

        _suppressSelectionUpdate = true;
        ListView_List.SelectedIndex = index;
        ListView_Details.SelectedIndex = index;
        ListView_Thumbs.SelectedIndex = index;
        _suppressSelectionUpdate = false;

        ActiveSelector?.Focus();
        if (ActiveSelector is ListBox lb && lb.SelectedItem != null)
            lb.ScrollIntoView(lb.SelectedItem);

        UpdatePositionDisplay();
        UpdatePreview(MediaItems[index]);
    }

    // ----------------- LIST EVENTS -----------------

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionUpdate) return;
        if (sender is Selector sel && sel == ActiveSelector)
        {
            var idx = sel.SelectedIndex;
            if (idx >= 0 && idx < MediaItems.Count)
            {
                _suppressSelectionUpdate = true;
                ListView_List.SelectedIndex = idx;
                ListView_Details.SelectedIndex = idx;
                ListView_Thumbs.SelectedIndex = idx;
                _suppressSelectionUpdate = false;

                UpdatePositionDisplay();
                UpdatePreview(MediaItems[idx]);
            }
            else
            {
                ClearPreview();
            }
        }
    }

    private void MediaList_KeyDown(object sender, KeyEventArgs e)
    {
        // Allow arrow keys to flow naturally for list selection,
        // but also explicitly handle them so all three list controls behave the same.
        if (e.Key == Key.Down || e.Key == Key.Right)
        {
            SelectIndex(GetSelectedIndex() + 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up || e.Key == Key.Left)
        {
            SelectIndex(GetSelectedIndex() - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            SelectIndex(0);
            e.Handled = true;
        }
        else if (e.Key == Key.End)
        {
            SelectIndex(MediaItems.Count - 1);
            e.Handled = true;
        }
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSliderUpdate) return;
        var idx = (int)Math.Round(e.NewValue);
        SelectIndex(idx);
    }

    private void UpdatePositionDisplay()
    {
        _suppressSliderUpdate = true;
        if (MediaItems.Count == 0)
        {
            PositionSlider.Maximum = 0;
            PositionSlider.Value = 0;
            PositionText.Text = "0/0";
        }
        else
        {
            PositionSlider.Maximum = MediaItems.Count - 1;
            var idx = Math.Max(0, GetSelectedIndex());
            PositionSlider.Value = idx;
            PositionText.Text = $"{idx + 1}/{MediaItems.Count}";
        }
        _suppressSliderUpdate = false;
    }

    // ----------------- PREVIEW -----------------

    private void UpdatePreview(MediaItem item)
    {
        PreviewTitle.Text = $"Preview — {item.FileName}";
        PreviewEmpty.Visibility = Visibility.Collapsed;

        try
        {
            if (item.Kind == MediaKind.Image)
            {
                PreviewVideo.Stop();
                PreviewVideo.Source = null;
                PreviewVideo.Visibility = Visibility.Collapsed;

                PreviewImage.Source = ThumbnailLoader.LoadFull(item.FullPath);
                PreviewImage.Visibility = Visibility.Visible;
            }
            else if (item.Kind == MediaKind.Video)
            {
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;

                PreviewVideo.Source = new Uri(item.FullPath, UriKind.Absolute);
                PreviewVideo.Visibility = Visibility.Visible;
                PreviewVideo.Play();
            }
            else
            {
                ClearPreview();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Preview error: {ex.Message}";
            ClearPreview();
        }
    }

    private void ClearPreview()
    {
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewVideo.Stop();
        PreviewVideo.Source = null;
        PreviewVideo.Visibility = Visibility.Collapsed;
        PreviewEmpty.Visibility = Visibility.Visible;
        PreviewTitle.Text = "Preview";
    }

    private void VideoPlay_Click(object sender, RoutedEventArgs e) => PreviewVideo.Play();
    private void VideoPause_Click(object sender, RoutedEventArgs e) => PreviewVideo.Pause();
    private void VideoStop_Click(object sender, RoutedEventArgs e) => PreviewVideo.Stop();

    // ----------------- DESTINATIONS -----------------

    private void AddDestination_Click(object sender, RoutedEventArgs e)
    {
        var dest = new DestinationButton();
        var dlg = new DestinationEditor(dest) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            Destinations.Add(dest);
            SaveSettings();
        }
    }

    private void EditDestination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DestinationButton dest)
        {
            var dlg = new DestinationEditor(dest) { Owner = this };
            if (dlg.ShowDialog() == true) SaveSettings();
        }
    }

    private void RemoveDestination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DestinationButton dest)
        {
            if (MessageBox.Show($"Remove destination '{dest.Name}'?", "Remove",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                Destinations.Remove(dest);
                SaveSettings();
            }
        }
    }

    private void SendToDestination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DestinationButton dest)
            MoveCurrentTo(dest);
    }

    private void MoveCurrentTo(DestinationButton dest)
    {
        var idx = GetSelectedIndex();
        if (idx < 0 || idx >= MediaItems.Count)
        {
            StatusText.Text = "No item selected.";
            return;
        }

        if (string.IsNullOrWhiteSpace(dest.FolderPath))
        {
            StatusText.Text = $"Destination '{dest.Name}' has no folder set.";
            return;
        }

        var item = MediaItems[idx];

        // Stop preview to release file handles for video
        if (item.Kind == MediaKind.Video)
        {
            PreviewVideo.Stop();
            PreviewVideo.Source = null;
        }

        try
        {
            var moved = FileMover.MoveToFolder(item.FullPath, dest.FolderPath);
            MediaItems.RemoveAt(idx);
            StatusText.Text = $"Moved to {moved}";

            if (MediaItems.Count > 0)
            {
                SelectIndex(Math.Min(idx, MediaItems.Count - 1));
            }
            else
            {
                ClearPreview();
                UpdatePositionDisplay();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Move failed: {ex.Message}", "MediaSort",
                MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = $"Move failed: {ex.Message}";
        }
    }

    // ----------------- GLOBAL HOTKEYS -----------------

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Global shortcut: Ctrl+, opens settings
        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Settings_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Don't hijack typing in textboxes / dialogs
        if (Keyboard.FocusedElement is TextBox) return;

        foreach (var dest in Destinations)
        {
            if (dest.HotKey == Key.None) continue;
            if (dest.HotKey == e.Key && Keyboard.Modifiers == dest.Modifiers)
            {
                MoveCurrentTo(dest);
                e.Handled = true;
                return;
            }
        }
    }

    // ----------------- SETTINGS / ABOUT -----------------

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings, Destinations) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            // Apply any source-folder change
            if (!string.IsNullOrWhiteSpace(_settings.SourceFolder)
                && _settings.SourceFolder != SourcePathText.Text)
            {
                SetSourceFolder(_settings.SourceFolder);
            }
            SaveSettings();
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutWindow { Owner = this };
        dlg.ShowDialog();
    }
}
