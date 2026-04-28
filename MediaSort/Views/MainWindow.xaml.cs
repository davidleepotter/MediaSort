using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaSort.Models;
using MediaSort.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using ListView = System.Windows.Controls.ListView;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using Cursors = System.Windows.Input.Cursors;

namespace MediaSort.Views;

public partial class MainWindow : Window
{
    /// <summary>The full unfiltered list (master). UI binds to FilteredItems.</summary>
    private readonly List<MediaItem> _allItems = new();

    /// <summary>The currently visible items (after search/filter). Bound to all 3 list views.</summary>
    public ObservableCollection<MediaItem> MediaItems { get; } = new();

    public ObservableCollection<DestinationButton> Destinations { get; } = new();

    private AppSettings _settings = new();
    private bool _suppressSliderUpdate;
    private bool _suppressSelectionUpdate;
    private bool _suppressVideoScrub;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private DispatcherTimer? _videoTimer;
    private FolderWatcher? _watcher;
    private readonly MoveHistoryService _history = new();
    private CancellationTokenSource? _probeCts;

    // Drag-and-drop state
    private System.Windows.Point? _dragStart;
    private bool _isDragging;

    // Image preview pan/zoom state
    private bool _imagePanning;
    private System.Windows.Point _imagePanStart;
    private double _imagePanStartH, _imagePanStartV;

    public MainWindow()
    {
        InitializeComponent();

        ListView_List.ItemsSource = MediaItems;
        ListView_Details.ItemsSource = MediaItems;
        ListView_Thumbs.ItemsSource = MediaItems;
        DestinationsPanel.ItemsSource = Destinations;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
    }

    // ----------------- LIFECYCLE -----------------

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Core.Initialize();
            _libVlc = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVlc);
            PreviewVideo.MediaPlayer = _mediaPlayer;

            _videoTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _videoTimer.Tick += VideoTimer_Tick;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Video playback unavailable: {ex.Message}";
        }

        _settings = SettingsService.Load();

        RecursiveCheck.IsChecked = _settings.RecursiveScan;
        ViewModeCombo.SelectedIndex = (int)_settings.ViewMode;
        SortKeyCombo.SelectedIndex = (int)_settings.SortKey;
        DateFilterCombo.SelectedIndex = (int)_settings.DateFilter;
        AspectFilterCombo.SelectedIndex = (int)_settings.AspectGroupFilter;
        UpdateSortDirButton();

        ThemeManager.ApplyOverride(_settings.ThemeOverride, _settings.AccentColor);

        foreach (var d in _settings.Destinations)
            Destinations.Add(SettingsService.FromSerializable(d));
        RefreshDestinationCounts();

        if (!string.IsNullOrWhiteSpace(_settings.SourceFolder) && Directory.Exists(_settings.SourceFolder))
        {
            SetSourceFolder(_settings.SourceFolder);
        }

        ApplyViewMode();
        Title = $"MediaSort v{VersionInfo.GetVersion()}";
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try { _videoTimer?.Stop(); } catch { }
        try { _mediaPlayer?.Stop(); } catch { }
        try { _mediaPlayer?.Dispose(); } catch { }
        try { _libVlc?.Dispose(); } catch { }
        try { _watcher?.Dispose(); } catch { }

        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.RecursiveScan = RecursiveCheck.IsChecked == true;
        if (ViewModeCombo.SelectedIndex >= 0)
            _settings.ViewMode = (ViewMode)ViewModeCombo.SelectedIndex;
        if (SortKeyCombo.SelectedIndex >= 0)
            _settings.SortKey = (SortKey)SortKeyCombo.SelectedIndex;
        if (DateFilterCombo.SelectedIndex >= 0)
            _settings.DateFilter = (DateFilterMode)DateFilterCombo.SelectedIndex;
        if (AspectFilterCombo.SelectedIndex >= 0)
            _settings.AspectGroupFilter = (AspectGroup)AspectFilterCombo.SelectedIndex;
        _settings.Destinations = Destinations.Select(SettingsService.ToSerializable).ToList();
        SettingsService.Save(_settings);
    }

    // ----------------- SORTING -----------------

    private void SortKeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (SortKeyCombo.SelectedIndex >= 0)
            _settings.SortKey = (SortKey)SortKeyCombo.SelectedIndex;
        ApplySort();
        SaveSettings();
    }

    private void SortDir_Click(object sender, RoutedEventArgs e)
    {
        _settings.SortDescending = !_settings.SortDescending;
        UpdateSortDirButton();
        ApplySort();
        SaveSettings();
    }

    private void GridHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader header) return;
        if (header.Tag is not string tag) return;
        if (!Enum.TryParse<SortKey>(tag, out var key)) return;

        if (_settings.SortKey == key)
            _settings.SortDescending = !_settings.SortDescending;
        else
        {
            _settings.SortKey = key;
            _settings.SortDescending = false;
        }
        SortKeyCombo.SelectedIndex = (int)key;
        UpdateSortDirButton();
        ApplySort();
        SaveSettings();
    }

    private void UpdateSortDirButton()
    {
        SortDirButton.Content = _settings.SortDescending ? "\u25BC" : "\u25B2";
        SortDirButton.ToolTip = _settings.SortDescending
            ? "Sort: Descending (click for Ascending)"
            : "Sort: Ascending (click for Descending)";
    }

    private static double AspectRatioFor(MediaItem m)
    {
        if (m.PixelHeight <= 0 || m.PixelWidth <= 0) return double.MaxValue;
        return (double)m.PixelWidth / m.PixelHeight;
    }

    private void ApplySort()
    {
        if (MediaItems.Count == 0) return;

        var selected = ActiveSelector?.SelectedItem as MediaItem;

        IOrderedEnumerable<MediaItem> ordered = _settings.SortKey switch
        {
            SortKey.Size     => _settings.SortDescending
                                ? MediaItems.OrderByDescending(m => m.SizeBytes)
                                : MediaItems.OrderBy(m => m.SizeBytes),
            SortKey.Aspect   => _settings.SortDescending
                                ? MediaItems.OrderByDescending(AspectRatioFor)
                                : MediaItems.OrderBy(AspectRatioFor),
            SortKey.Modified => _settings.SortDescending
                                ? MediaItems.OrderByDescending(m => m.ModifiedDate)
                                : MediaItems.OrderBy(m => m.ModifiedDate),
            SortKey.Kind     => _settings.SortDescending
                                ? MediaItems.OrderByDescending(m => m.Kind.ToString())
                                : MediaItems.OrderBy(m => m.Kind.ToString()),
            SortKey.Duration => _settings.SortDescending
                                ? MediaItems.OrderByDescending(m => m.DurationSeconds)
                                : MediaItems.OrderBy(m => m.DurationSeconds),
            _                => _settings.SortDescending
                                ? MediaItems.OrderByDescending(m => m.FileName, StringComparer.OrdinalIgnoreCase)
                                : MediaItems.OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase),
        };

        var sorted = (_settings.SortKey == SortKey.Name
                        ? ordered
                        : ordered.ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase))
                     .ToList();

        _suppressSelectionUpdate = true;
        MediaItems.Clear();
        foreach (var m in sorted) MediaItems.Add(m);
        _suppressSelectionUpdate = false;

        var newIdx = selected != null ? MediaItems.IndexOf(selected) : -1;
        if (newIdx < 0 && MediaItems.Count > 0) newIdx = 0;
        if (newIdx >= 0) SelectIndex(newIdx);
        UpdatePositionDisplay();
    }

    // ----------------- FILTERING / SEARCH -----------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
    }

    private void DateFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (DateFilterCombo.SelectedIndex >= 0)
            _settings.DateFilter = (DateFilterMode)DateFilterCombo.SelectedIndex;
        ApplyFilter();
        SaveSettings();
    }

    private void AspectFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (AspectFilterCombo.SelectedIndex >= 0)
            _settings.AspectGroupFilter = (AspectGroup)AspectFilterCombo.SelectedIndex;
        ApplyFilter();
        SaveSettings();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim().ToLowerInvariant() ?? "";
        var dateCutoff = DateTime.MinValue;
        switch (_settings.DateFilter)
        {
            case DateFilterMode.Last7Days: dateCutoff = DateTime.Now.AddDays(-7); break;
            case DateFilterMode.Last30Days: dateCutoff = DateTime.Now.AddDays(-30); break;
            case DateFilterMode.ThisYear: dateCutoff = new DateTime(DateTime.Now.Year, 1, 1); break;
        }

        var filtered = _allItems.Where(m =>
        {
            if (!string.IsNullOrEmpty(query) && !m.FileName.ToLowerInvariant().Contains(query)) return false;
            if (m.ModifiedDate < dateCutoff) return false;
            switch (_settings.AspectGroupFilter)
            {
                case AspectGroup.Portrait when m.AspectBucket != "Portrait": return false;
                case AspectGroup.Landscape when m.AspectBucket != "Landscape": return false;
                case AspectGroup.Square when m.AspectBucket != "Square": return false;
            }
            return true;
        }).ToList();

        _suppressSelectionUpdate = true;
        MediaItems.Clear();
        foreach (var m in filtered) MediaItems.Add(m);
        _suppressSelectionUpdate = false;

        ApplySort();
        UpdatePositionDisplay();
        UpdateStats();
        if (MediaItems.Count > 0) SelectIndex(0);
        else ClearPreview();
    }

    private void UpdateStats()
    {
        var total = _allItems.Count;
        var shown = MediaItems.Count;
        var sel = ActiveSelector?.SelectedItems.Count ?? 0;
        long size = MediaItems.Sum(m => m.SizeBytes);
        StatsText.Text = $"{shown} of {total} shown · {sel} selected · {FormatBytes(size)}";
    }

    private static string FormatBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double s = b; int i = 0;
        while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
        return $"{s:0.##} {u[i]}";
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

    private void RegenThumbnails_Click(object sender, RoutedEventArgs e)
    {
        if (_allItems.Count == 0)
        {
            StatusText.Text = "No items in source list to regenerate.";
            return;
        }

        // Cancel any in-flight probe and start a fresh one.
        try { _probeCts?.Cancel(); } catch { }
        _probeCts = new CancellationTokenSource();

        // Wipe existing thumbnails so the placeholder shows during regen.
        foreach (var item in _allItems) item.Thumbnail = null;

        StatusText.Text = $"Regenerating thumbnails for {_allItems.Count} item(s)...";
        CrashLogger.Info($"regen-thumbs requested count={_allItems.Count}");
        StartBackgroundProbe(_allItems.ToList(), _probeCts.Token);
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
        // Cancel any in-flight probe from the previous folder so we don't pile up
        // dispatcher work (especially LibVLC video probes) onto the UI thread.
        try { _probeCts?.Cancel(); } catch { }
        _probeCts = new CancellationTokenSource();

        // Stop any video that is still playing from the previous selection so
        // LibVLC isn't holding a file handle while we switch folders.
        StopVideo();

        SourcePathText.Text = folder;
        _allItems.Clear();
        MediaItems.Clear();

        var items = MediaScanner.Scan(folder, RecursiveCheck.IsChecked == true).ToList();
        _allItems.AddRange(items);
        ApplyFilter(); // also applies sort

        StatusText.Text = $"{_allItems.Count} media file(s) found";

        if (MediaItems.Count > 0) SelectIndex(0); else ClearPreview();

        // Background pass for thumbnails + dimensions
        StartBackgroundProbe(items, _probeCts.Token);

        // Watch the folder for changes (debounced refresh)
        if (_settings.WatchSourceFolder)
        {
            _watcher ??= new FolderWatcher(Dispatcher);
            _watcher.Changed -= OnWatchedFolderChanged;
            _watcher.Changed += OnWatchedFolderChanged;
            _watcher.Watch(folder, RecursiveCheck.IsChecked == true);
        }
        else
        {
            _watcher?.Stop();
        }
    }

    private void OnWatchedFolderChanged()
    {
        if (string.IsNullOrWhiteSpace(_settings.SourceFolder)) return;
        // Re-scan only the file list (don't disturb thumbnails of items still present)
        var fresh = MediaScanner.Scan(_settings.SourceFolder, RecursiveCheck.IsChecked == true)
                                .Select(m => m.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existing = _allItems.Select(m => m.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = fresh.Except(existing).Select(p => new MediaItem(p)).ToList();
        var removed = _allItems.Where(m => !fresh.Contains(m.FullPath)).ToList();

        if (added.Count == 0 && removed.Count == 0) return;

        foreach (var r in removed) _allItems.Remove(r);
        _allItems.AddRange(added);
        ApplyFilter();

        if (added.Count > 0) StartBackgroundProbe(added, _probeCts?.Token ?? CancellationToken.None);
    }

    private void StartBackgroundProbe(List<MediaItem> items, CancellationToken ct)
    {
        var imageCount = items.Count(i => i.Kind == MediaKind.Image);
        var videoCount = items.Count(i => i.Kind == MediaKind.Video);
        CrashLogger.Info($"probe:start total={items.Count} images={imageCount} videos={videoCount}");

        _ = Task.Run(() =>
        {
            int thumbsAssigned = 0;
            int dimsAssigned = 0;
            int failures = 0;
            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) { CrashLogger.Info("probe:cancelled"); return; }
                try
                {
                    if (item.Kind == MediaKind.Image)
                    {
                        if (ProbeImage(item, ct)) thumbsAssigned++;
                        if (item.PixelWidth > 0) dimsAssigned++;
                    }
                    else if (item.Kind == MediaKind.Video)
                    {
                        ProbeVideo(item, ct);
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    CrashLogger.Log(ex, $"probe:{item.FullPath}");
                }
            }

            CrashLogger.Info($"probe:done thumbs={thumbsAssigned} dims={dimsAssigned} failures={failures}");

            if (ct.IsCancellationRequested) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_settings.SortKey == SortKey.Aspect || _settings.SortKey == SortKey.Duration)
                        ApplySort();
                }));
            }
            catch (Exception ex) { CrashLogger.Log(ex, "probe:final-sort"); }
        }, ct);
    }

    /// <summary>Returns true if a thumbnail was successfully decoded and assigned.</summary>
    private bool ProbeImage(MediaItem item, CancellationToken ct)
    {
        // Dimensions (cheap, metadata-only)
        try
        {
            var (w, h) = ThumbnailLoader.TryReadImageDimensions(item.FullPath);
            if (w > 0 && h > 0 && !ct.IsCancellationRequested)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    item.PixelWidth = w;
                    item.PixelHeight = h;
                }));
            }
        }
        catch (Exception ex) { CrashLogger.Log(ex, $"dims:{item.FullPath}"); }

        if (ct.IsCancellationRequested) return false;

        // Thumbnail: read bytes + decode on the background thread (BitmapImage.Freeze
        // makes the result safe to hand to the UI thread). Assign on UI thread only.
        BitmapImage? thumb = null;
        try
        {
            var bytes = ThumbnailLoader.TryReadAllBytes(item.FullPath);
            if (bytes == null)
            {
                CrashLogger.Info($"probe:read-null {item.FileName}");
                return false;
            }
            thumb = ThumbnailLoader.BitmapFromBytes(bytes, 128);
            if (thumb == null)
            {
                CrashLogger.Info($"probe:decode-null {item.FileName} bytes={bytes.Length}");
                return false;
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"probe-image-bg:{item.FullPath}");
            return false;
        }

        if (ct.IsCancellationRequested) return false;

        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ct.IsCancellationRequested) return;
                item.Thumbnail = thumb;
            }));
            return true;
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"dispatch-assign:{item.FullPath}");
            return false;
        }
    }

    private void ProbeVideo(MediaItem item, CancellationToken ct)
    {
        try
        {
            var (w, h, dur) = VideoProbe.TryReadVideoInfo(item.FullPath);
            if (ct.IsCancellationRequested) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (w > 0 && h > 0) { item.PixelWidth = w; item.PixelHeight = h; }
                if (dur > 0) item.DurationSeconds = dur;
            }));
        }
        catch (Exception ex) { CrashLogger.Log(ex, $"video-probe:{item.FullPath}"); }
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

        var idx = GetSelectedIndex();
        if (MediaItems.Count > 0)
            SelectIndex(idx >= 0 ? idx : 0);
    }

    private ListBox? ActiveSelector =>
        (ViewMode)ViewModeCombo.SelectedIndex switch
        {
            ViewMode.List => (ListBox?)ListView_List,
            ViewMode.Details => ListView_Details,
            ViewMode.Thumbnails => ListView_Thumbs,
            _ => null
        };

    private int GetSelectedIndex() => ActiveSelector?.SelectedIndex ?? -1;

    private List<MediaItem> GetSelectedItems()
    {
        var sel = ActiveSelector?.SelectedItems;
        if (sel == null) return new List<MediaItem>();
        return sel.OfType<MediaItem>().ToList();
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
        switch (ActiveSelector)
        {
            case ListBox lb when lb.SelectedItem != null:
                lb.ScrollIntoView(lb.SelectedItem);
                break;
            case ListView lv when lv.SelectedItem != null:
                lv.ScrollIntoView(lv.SelectedItem);
                break;
        }

        UpdatePositionDisplay();
        UpdatePreview(MediaItems[index]);
        UpdateStats();
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
                UpdatePositionDisplay();
                UpdatePreview(MediaItems[idx]);
                UpdateStats();
            }
            else
            {
                ClearPreview();
                UpdateStats();
            }
        }
    }

    private void MediaList_KeyDown(object sender, KeyEventArgs e)
    {
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

    // ----------------- DRAG-AND-DROP -----------------

    private void MediaList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _isDragging = false;
    }

    private void MediaList_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isDragging || _dragStart == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var items = GetSelectedItems();
        if (items.Count == 0) return;

        _isDragging = true;
        try
        {
            var paths = items.Select(i => i.FullPath).ToArray();
            var data = new DataObject(DataFormats.FileDrop, paths);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _isDragging = false;
            _dragStart = null;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        // Only handle drops on the main window background — not when dropped on destinations
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths == null || paths.Length == 0) return;

        // If a single folder is dropped, set it as source
        if (paths.Length == 1 && Directory.Exists(paths[0]))
        {
            SetSourceFolder(paths[0]);
            _settings.SourceFolder = paths[0];
            SaveSettings();
            return;
        }

        // Otherwise: treat as media files to add (parent folder of first becomes source if none set)
        if (string.IsNullOrWhiteSpace(_settings.SourceFolder))
        {
            var parent = Path.GetDirectoryName(paths[0]);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                SetSourceFolder(parent);
                _settings.SourceFolder = parent;
                SaveSettings();
            }
        }
        else
        {
            // Just refresh in case the dropped files came from outside but the user wants a refresh
            Refresh_Click(this, new RoutedEventArgs());
        }
    }

    private void Destination_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void Destination_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe) fe.Opacity = 0.7;
    }

    private void Destination_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe) fe.Opacity = 1.0;
    }

    private void Destination_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe) fe.Opacity = 1.0;
        if (sender is not FrameworkElement target || target.Tag is not DestinationButton dest) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
        if (paths == null || paths.Length == 0) return;

        // Map dropped paths back to MediaItems if they belong to current source
        var items = paths
            .Select(p => _allItems.FirstOrDefault(m => string.Equals(m.FullPath, p, StringComparison.OrdinalIgnoreCase)))
            .Where(m => m != null)
            .Cast<MediaItem>()
            .ToList();

        if (items.Count == 0)
        {
            // External drop: move using paths directly
            var batch = new List<MoveHistoryService.MoveRecord>();
            foreach (var p in paths.Where(File.Exists))
            {
                var r = MoveWithPolicy(p, dest, ref _conflictPolicyForBatch, ref _applyToAllForBatch);
                if (r != null) batch.Add(r);
            }
            if (batch.Count > 0) _history.Push(batch);
            UndoButton.IsEnabled = _history.CanUndo;
            return;
        }

        MoveItemsTo(items, dest, target);
    }

    // ----------------- PREVIEW -----------------

    private void UpdatePreview(MediaItem item)
    {
        PreviewTitle.Text = $"Preview — {item.FileName}";

        try
        {
            if (item.Kind == MediaKind.Image)
            {
                StopVideo();
                PreviewVideo.Visibility = Visibility.Collapsed;
                VideoControls.Visibility = Visibility.Collapsed;

                var bmp = ThumbnailLoader.LoadFull(item.FullPath);
                if (bmp == null)
                {
                    PreviewImage.Source = null;
                    PreviewImage.Visibility = Visibility.Collapsed;
                    PreviewEmpty.Text = $"Cannot decode {item.Extension}";
                    PreviewEmpty.Visibility = Visibility.Visible;
                    StatusText.Text = $"Cannot decode {item.FileName}";
                    return;
                }

                PreviewImage.Source = bmp;
                PreviewImage.Visibility = Visibility.Visible;
                PreviewImageScale.ScaleX = 1.0;
                PreviewImageScale.ScaleY = 1.0;
                PreviewScroll.ScrollToHorizontalOffset(0);
                PreviewScroll.ScrollToVerticalOffset(0);
                PreviewEmpty.Visibility = Visibility.Collapsed;

                PopulateExif(item);
            }
            else if (item.Kind == MediaKind.Video)
            {
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;

                if (_mediaPlayer == null || _libVlc == null)
                {
                    PreviewEmpty.Text = "Video playback unavailable (LibVLC failed to initialize).";
                    PreviewEmpty.Visibility = Visibility.Visible;
                    PreviewVideo.Visibility = Visibility.Collapsed;
                    VideoControls.Visibility = Visibility.Collapsed;
                    return;
                }

                PreviewVideo.Visibility = Visibility.Visible;
                VideoControls.Visibility = Visibility.Visible;
                PreviewEmpty.Visibility = Visibility.Collapsed;

                // Don't call _mediaPlayer.Stop() here — LibVLCSharp's Play(newMedia)
                // already replaces the current media internally, and Stop() is a
                // synchronous, blocking call that can hang the UI thread.
                try { _videoTimer?.Stop(); } catch { }
                using var media = new Media(_libVlc, new Uri(item.FullPath));
                _mediaPlayer.Play(media);
                _videoTimer?.Start();

                PopulateExif(item); // shows file info even for videos (no EXIF, but file size etc)
            }
            else
            {
                ClearPreview();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Preview error: {ex.Message}";
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewVideo.Visibility = Visibility.Collapsed;
            VideoControls.Visibility = Visibility.Collapsed;
            PreviewEmpty.Text = $"Preview error: {ex.Message}";
            PreviewEmpty.Visibility = Visibility.Visible;
        }
    }

    private void PopulateExif(MediaItem item)
    {
        var rows = new List<KeyValuePair<string, string>>
        {
            new("Path", item.FullPath),
            new("Size", item.SizeDisplay),
            new("Modified", item.ModifiedDate.ToString("yyyy-MM-dd HH:mm")),
            new("Kind", item.Kind.ToString()),
        };
        if (item.PixelWidth > 0 && item.PixelHeight > 0)
            rows.Add(new("Dimensions", $"{item.PixelWidth} \u00D7 {item.PixelHeight} ({item.AspectDisplay})"));
        if (item.DurationSeconds > 0)
            rows.Add(new("Duration", item.DurationDisplay));
        if (item.Kind == MediaKind.Image)
        {
            rows.AddRange(ExifReader.Read(item.FullPath));
        }
        ExifItems.ItemsSource = rows;
    }

    private void ClearPreview()
    {
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        StopVideo();
        PreviewVideo.Visibility = Visibility.Collapsed;
        VideoControls.Visibility = Visibility.Collapsed;
        PreviewEmpty.Text = "Select a media file to preview";
        PreviewEmpty.Visibility = Visibility.Visible;
        PreviewTitle.Text = "Preview";
        ExifItems.ItemsSource = null;
    }

    private void StopVideo()
    {
        // Tick timer is UI-thread; stopping it is cheap.
        try { _videoTimer?.Stop(); } catch { }

        // _mediaPlayer.Stop() is SYNCHRONOUS in LibVLCSharp and blocks the calling
        // thread until VLC's internal threads tear down the playback pipeline.
        // On the UI thread that can freeze the app for seconds (or indefinitely
        // on a wedged stream). Run it on a background thread so folder switching
        // stays responsive.
        var mp = _mediaPlayer;
        if (mp == null) return;
        Task.Run(() =>
        {
            try { mp.Stop(); } catch { }
        });
    }

    private void VideoTogglePlay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_mediaPlayer == null) return;
            if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
            else _mediaPlayer.Play();
        }
        catch { }
    }

    private void VideoStop_Click(object sender, RoutedEventArgs e) => StopVideo();

    private void VideoTimer_Tick(object? sender, EventArgs e)
    {
        if (_mediaPlayer == null) return;
        try
        {
            var dur = _mediaPlayer.Length; // ms
            var pos = _mediaPlayer.Time;   // ms

            if (!_suppressVideoScrub && dur > 0)
            {
                _suppressVideoScrub = true;
                VideoScrub.Maximum = dur;
                VideoScrub.Value = pos;
                _suppressVideoScrub = false;
            }
            VideoTimeText.Text = $"{Format(pos)} / {Format(dur)}";

            // Loop handling
            if (LoopToggle.IsChecked == true && dur > 0 && pos > 0 && (dur - pos) < 250)
            {
                _mediaPlayer.Stop();
                var idx = GetSelectedIndex();
                if (idx >= 0 && idx < MediaItems.Count)
                {
                    var item = MediaItems[idx];
                    if (item.Kind == MediaKind.Video && _libVlc != null)
                    {
                        using var media = new Media(_libVlc, new Uri(item.FullPath));
                        _mediaPlayer.Play(media);
                    }
                }
            }
        }
        catch { }

        static string Format(long ms)
        {
            if (ms < 0) ms = 0;
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }
    }

    private void VideoScrub_DragStarted(object sender, RoutedEventArgs e)
    {
        _suppressVideoScrub = true;
    }

    private void VideoScrub_DragCompleted(object sender, RoutedEventArgs e)
    {
        try { _mediaPlayer?.SeekTo(TimeSpan.FromMilliseconds(VideoScrub.Value)); } catch { }
        _suppressVideoScrub = false;
    }

    private void VideoScrub_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Click-to-seek
        if (sender is Slider s && _mediaPlayer != null && s.Maximum > 0)
        {
            var pt = e.GetPosition(s);
            var ratio = pt.X / s.ActualWidth;
            var newMs = (long)(ratio * s.Maximum);
            try { _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(newMs)); } catch { }
        }
    }

    // ----------------- IMAGE ZOOM / PAN -----------------

    private void PreviewImage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        var newScale = Math.Max(0.1, Math.Min(10.0, PreviewImageScale.ScaleX * factor));
        PreviewImageScale.ScaleX = newScale;
        PreviewImageScale.ScaleY = newScale;
        e.Handled = true;
    }

    private void PreviewImage_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _imagePanning = true;
        _imagePanStart = e.GetPosition(PreviewScroll);
        _imagePanStartH = PreviewScroll.HorizontalOffset;
        _imagePanStartV = PreviewScroll.VerticalOffset;
        PreviewImage.CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    private void PreviewImage_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _imagePanning = false;
        PreviewImage.ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    private void PreviewImage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_imagePanning) return;
        var p = e.GetPosition(PreviewScroll);
        PreviewScroll.ScrollToHorizontalOffset(_imagePanStartH - (p.X - _imagePanStart.X));
        PreviewScroll.ScrollToVerticalOffset(_imagePanStartV - (p.Y - _imagePanStart.Y));
    }

    private void ResetImageZoom()
    {
        PreviewImageScale.ScaleX = 1.0;
        PreviewImageScale.ScaleY = 1.0;
        PreviewScroll.ScrollToHorizontalOffset(0);
        PreviewScroll.ScrollToVerticalOffset(0);
    }

    // ----------------- DESTINATIONS -----------------

    private ConflictPolicy _conflictPolicyForBatch = ConflictPolicy.Prompt;
    private bool _applyToAllForBatch = false;

    private void AddDestination_Click(object sender, RoutedEventArgs e)
    {
        var dest = new DestinationButton();
        var dlg = new DestinationEditor(dest) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            Destinations.Add(dest);
            RefreshDestinationCounts();
            SaveSettings();
        }
    }

    private void EditDestination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DestinationButton dest)
        {
            var dlg = new DestinationEditor(dest) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                RefreshDestinationCounts();
                SaveSettings();
            }
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

    private void MoveDestUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DestinationButton dest)
        {
            var i = Destinations.IndexOf(dest);
            if (i > 0)
            {
                Destinations.Move(i, i - 1);
                SaveSettings();
            }
        }
    }

    private void MoveDestDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DestinationButton dest)
        {
            var i = Destinations.IndexOf(dest);
            if (i >= 0 && i < Destinations.Count - 1)
            {
                Destinations.Move(i, i + 1);
                SaveSettings();
            }
        }
    }

    private void OpenDestFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DestinationButton dest)
        {
            if (Directory.Exists(dest.FolderPath))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{dest.FolderPath}\"");
        }
    }

    private void RefreshDestinationCounts()
    {
        foreach (var d in Destinations)
        {
            try
            {
                d.ItemCount = Directory.Exists(d.FolderPath)
                    ? Directory.EnumerateFiles(d.FolderPath, "*.*", SearchOption.TopDirectoryOnly)
                        .Count(f => MediaFormats.AllExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    : 0;
            }
            catch { d.ItemCount = 0; }
        }
    }

    private void SendToDestination_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is DestinationButton dest)
        {
            var items = GetSelectedItems();
            if (items.Count == 0) { StatusText.Text = "No item selected."; return; }
            MoveItemsTo(items, dest, fe);
        }
    }

    private void MoveItemsTo(List<MediaItem> items, DestinationButton dest, FrameworkElement? destinationElement)
    {
        if (string.IsNullOrWhiteSpace(dest.FolderPath))
        {
            StatusText.Text = $"Destination '{dest.Name}' has no folder set.";
            return;
        }

        // Kind filter
        if (!string.IsNullOrEmpty(dest.KindFilter))
        {
            var blocked = items.Where(i => i.Kind.ToString() != dest.KindFilter).ToList();
            if (blocked.Count > 0)
            {
                StatusText.Text = $"'{dest.Name}' only accepts {dest.KindFilter} files. Skipped {blocked.Count}.";
                items = items.Where(i => i.Kind.ToString() == dest.KindFilter).ToList();
                if (items.Count == 0) return;
            }
        }

        // Animation (only for first item — single-select case looks best)
        if (items.Count == 1)
        {
            var sourceElement = GetSelectedItemElement();
            var destEl = destinationElement ?? FindDestinationElement(dest);
            TryPlayMoveAnimation(items[0], sourceElement, destEl);
        }

        // Release file handles
        StopVideo();
        PreviewImage.Source = null;

        var firstIdx = MediaItems.IndexOf(items[0]);
        var batch = new List<MoveHistoryService.MoveRecord>();

        // Reset per-batch conflict prefs
        _conflictPolicyForBatch = SettingPolicyToConflict(_settings.ConflictPolicy);
        _applyToAllForBatch = _conflictPolicyForBatch != ConflictPolicy.Prompt;

        foreach (var item in items.ToList())
        {
            var rec = MoveWithPolicy(item.FullPath, dest, ref _conflictPolicyForBatch, ref _applyToAllForBatch);
            if (rec == null) continue; // user cancelled or skipped
            batch.Add(rec);
            // remove from collections
            _allItems.Remove(item);
            MediaItems.Remove(item);
        }

        if (batch.Count > 0)
        {
            _history.Push(batch);
            UndoButton.IsEnabled = _history.CanUndo;
            StatusText.Text = $"Moved {batch.Count} file(s) to {dest.Name}";
            RefreshDestinationCounts();
        }

        if (MediaItems.Count > 0)
        {
            SelectIndex(Math.Min(firstIdx, MediaItems.Count - 1));
        }
        else
        {
            ClearPreview();
            UpdatePositionDisplay();
        }
        UpdateStats();
    }

    private static ConflictPolicy SettingPolicyToConflict(ConflictPolicySetting s) => s switch
    {
        ConflictPolicySetting.AlwaysRename => ConflictPolicy.Rename,
        ConflictPolicySetting.AlwaysOverwrite => ConflictPolicy.Overwrite,
        ConflictPolicySetting.AlwaysSkip => ConflictPolicy.Skip,
        _ => ConflictPolicy.Prompt
    };

    /// <summary>
    /// Move a single file using the dest's subfolder + rename templates, applying the
    /// current batch conflict policy (and prompting if unset). Returns the move record
    /// on success, null on skip/cancel/failure.
    /// </summary>
    private MoveHistoryService.MoveRecord? MoveWithPolicy(string sourcePath,
                                                          DestinationButton dest,
                                                          ref ConflictPolicy policy,
                                                          ref bool applyAll)
    {
        var targetFolder = dest.FolderPath;
        if (!string.IsNullOrEmpty(dest.SubfolderTemplate))
        {
            var fi = new FileInfo(sourcePath);
            var date = fi.Exists ? fi.LastWriteTime : DateTime.Now;
            var sub = System.Text.RegularExpressions.Regex.Replace(dest.SubfolderTemplate,
                @"\{(\w+)(?::([^}]+))?\}", m =>
                {
                    var token = m.Groups[1].Value.ToLowerInvariant();
                    var fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
                    return token switch
                    {
                        "date" => date.ToString(string.IsNullOrEmpty(fmt) ? "yyyy-MM" : fmt),
                        _ => m.Value
                    };
                });
            foreach (var c in Path.GetInvalidFileNameChars()) sub = sub.Replace(c.ToString(), "");
            targetFolder = Path.Combine(targetFolder, sub);
        }

        // Decide policy: if Prompt and a conflict will occur, ask once (then maybe apply to all)
        var fileNamePreview = string.IsNullOrEmpty(dest.RenameTemplate)
            ? Path.GetFileName(sourcePath)
            : Path.GetFileName(Path.Combine(targetFolder, "x")); // template applied later

        var probable = Path.Combine(targetFolder, Path.GetFileName(sourcePath));
        if (File.Exists(probable) && policy == ConflictPolicy.Prompt && !applyAll)
        {
            var dlg = new ConflictDialog(Path.GetFileName(sourcePath), targetFolder) { Owner = this };
            if (dlg.ShowDialog() != true) return null;
            policy = dlg.Choice;
            applyAll = dlg.ApplyToAll;
            if (!applyAll)
            {
                // Single-file decision: apply once, then reset to Prompt for next conflict
                var oneShot = policy;
                policy = ConflictPolicy.Prompt;
                var rec1 = DoMove(sourcePath, targetFolder, dest.RenameTemplate, oneShot);
                return rec1;
            }
        }

        return DoMove(sourcePath, targetFolder, dest.RenameTemplate, policy);
    }

    private MoveHistoryService.MoveRecord? DoMove(string sourcePath, string targetFolder, string? rename, ConflictPolicy policy)
    {
        var r = FileMover.MoveToFolder(sourcePath, targetFolder, policy, rename);
        if (r.Outcome == MoveOutcome.Moved)
        {
            return new MoveHistoryService.MoveRecord
            {
                OriginalPath = sourcePath,
                NewPath = r.FinalPath,
                When = DateTime.Now,
                Action = "Move"
            };
        }
        if (r.Outcome == MoveOutcome.Failed)
        {
            StatusText.Text = $"Move failed: {r.ErrorMessage}";
        }
        return null;
    }

    // ----------------- UNDO / TRASH / SKIP -----------------

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var batch = _history.PopUndo();
        if (batch == null) { StatusText.Text = "Nothing to undo."; return; }

        int restored = 0;
        foreach (var rec in batch)
        {
            if (rec.Action == "Move")
            {
                var r = FileMover.UndoMove(rec.NewPath, rec.OriginalPath);
                if (r.Outcome == MoveOutcome.Moved)
                {
                    var item = new MediaItem(r.FinalPath);
                    _allItems.Add(item);
                    restored++;
                    StartBackgroundProbe(new List<MediaItem> { item }, _probeCts?.Token ?? CancellationToken.None);
                }
            }
            else if (rec.Action == "Trash")
            {
                // Recycle bin restore is not directly supported; flag for user
                StatusText.Text = "Note: items sent to Recycle Bin must be restored manually from Windows.";
            }
        }

        ApplyFilter();
        UndoButton.IsEnabled = _history.CanUndo;
        StatusText.Text = $"Undone — restored {restored} file(s)";
        RefreshDestinationCounts();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (MediaItems.Count == 0) return;
        SelectIndex(Math.Min(GetSelectedIndex() + 1, MediaItems.Count - 1));
    }

    private void Trash_Click(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedItems();
        if (items.Count == 0) return;

        var msg = items.Count == 1
            ? $"Send '{items[0].FileName}' to the Recycle Bin?"
            : $"Send {items.Count} files to the Recycle Bin?";
        if (MessageBox.Show(msg, "Trash", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        StopVideo();
        PreviewImage.Source = null;

        var firstIdx = MediaItems.IndexOf(items[0]);
        var batch = new List<MoveHistoryService.MoveRecord>();
        foreach (var item in items.ToList())
        {
            if (FileMover.SendToRecycleBin(item.FullPath))
            {
                batch.Add(new MoveHistoryService.MoveRecord
                {
                    OriginalPath = item.FullPath,
                    NewPath = "(recycle bin)",
                    Action = "Trash"
                });
                _allItems.Remove(item);
                MediaItems.Remove(item);
            }
        }

        if (batch.Count > 0)
        {
            _history.Push(batch);
            UndoButton.IsEnabled = _history.CanUndo;
            StatusText.Text = $"Sent {batch.Count} file(s) to Recycle Bin";
        }

        if (MediaItems.Count > 0)
            SelectIndex(Math.Min(firstIdx, MediaItems.Count - 1));
        else { ClearPreview(); UpdatePositionDisplay(); }
        UpdateStats();
    }

    // ----------------- DUPLICATES -----------------

    private async void FindDuplicates_Click(object sender, RoutedEventArgs e)
    {
        var images = _allItems.Where(m => m.Kind == MediaKind.Image).ToList();
        if (images.Count < 2) { StatusText.Text = "Need at least 2 images to compare."; return; }

        StatusText.Text = "Hashing images...";
        await Task.Run(() =>
        {
            foreach (var m in images.Where(m => string.IsNullOrEmpty(m.PerceptualHash)))
            {
                var h = PerceptualHasher.Hash(m.FullPath);
                Dispatcher.Invoke(() => m.PerceptualHash = h);
            }
        });

        // Pairwise compare
        int dupCount = 0;
        foreach (var m in images) m.IsDuplicate = false;
        for (int i = 0; i < images.Count; i++)
        {
            for (int j = i + 1; j < images.Count; j++)
            {
                if (PerceptualHasher.Distance(images[i].PerceptualHash, images[j].PerceptualHash) <= 6)
                {
                    if (!images[i].IsDuplicate) { images[i].IsDuplicate = true; dupCount++; }
                    if (!images[j].IsDuplicate) { images[j].IsDuplicate = true; dupCount++; }
                }
            }
        }
        StatusText.Text = dupCount > 0
            ? $"Flagged {dupCount} potential duplicate(s) — see DUP badge in Thumbnails view"
            : "No near-duplicates found";
    }

    // ----------------- CONTEXT MENU ACTIONS -----------------

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelectedItems().FirstOrDefault();
        if (sel == null) return;
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{sel.FullPath}\""); }
        catch (Exception ex) { StatusText.Text = $"Open failed: {ex.Message}"; }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var sel = GetSelectedItems();
        if (sel.Count == 0) return;
        Clipboard.SetText(string.Join("\n", sel.Select(i => i.FullPath)));
        StatusText.Text = $"Copied {sel.Count} path(s) to clipboard";
    }

    // ----------------- MOVE ANIMATION -----------------

    private FrameworkElement? GetSelectedItemElement()
    {
        var selector = ActiveSelector;
        if (selector == null || selector.SelectedItem == null) return null;
        return selector.ItemContainerGenerator.ContainerFromItem(selector.SelectedItem) as FrameworkElement;
    }

    private FrameworkElement? FindDestinationElement(DestinationButton dest)
    {
        if (DestinationsPanel == null) return null;
        return DestinationsPanel.ItemContainerGenerator.ContainerFromItem(dest) as FrameworkElement;
    }

    private ImageSource? CaptureItemVisual(MediaItem item, FrameworkElement? sourceElement)
    {
        if (item.Kind == MediaKind.Image && PreviewImage.Source is ImageSource imgSrc &&
            PreviewImage.IsVisible)
        {
            return imgSrc;
        }
        if (item.Thumbnail != null) return item.Thumbnail;
        if (sourceElement != null && sourceElement.ActualWidth > 0 && sourceElement.ActualHeight > 0)
        {
            try
            {
                var rtb = new RenderTargetBitmap(
                    (int)sourceElement.ActualWidth,
                    (int)sourceElement.ActualHeight,
                    96, 96, PixelFormats.Pbgra32);
                rtb.Render(sourceElement);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }
        }
        return null;
    }

    private void TryPlayMoveAnimation(MediaItem item,
                                      FrameworkElement? sourceElement,
                                      FrameworkElement? destElement)
    {
        try
        {
            if (AnimationOverlay == null) return;
            if (sourceElement == null || destElement == null) return;
            if (sourceElement.ActualWidth <= 0 || sourceElement.ActualHeight <= 0) return;
            if (destElement.ActualWidth <= 0 || destElement.ActualHeight <= 0) return;

            var visual = CaptureItemVisual(item, sourceElement);
            if (visual == null) return;

            var srcTopLeft = sourceElement.TranslatePoint(new System.Windows.Point(0, 0), AnimationOverlay);
            var dstTopLeft = destElement.TranslatePoint(new System.Windows.Point(0, 0), AnimationOverlay);

            var srcW = sourceElement.ActualWidth;
            var srcH = sourceElement.ActualHeight;
            var dstW = destElement.ActualWidth;
            var dstH = destElement.ActualHeight;

            var ghost = new System.Windows.Controls.Image
            {
                Source = visual,
                Stretch = Stretch.Uniform,
                Width = srcW,
                Height = srcH,
                Opacity = 0.95,
                IsHitTestVisible = false
            };
            ghost.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 0,
                Opacity = 0.55,
                Color = Colors.Black
            };

            Canvas.SetLeft(ghost, srcTopLeft.X);
            Canvas.SetTop(ghost, srcTopLeft.Y);
            AnimationOverlay.Children.Add(ghost);

            var duration = TimeSpan.FromMilliseconds(Math.Max(60, _settings.AnimationDurationMs));
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            var leftAnim = new DoubleAnimation(srcTopLeft.X, dstTopLeft.X, duration) { EasingFunction = ease };
            var topAnim = new DoubleAnimation(srcTopLeft.Y, dstTopLeft.Y, duration) { EasingFunction = ease };
            var widthAnim = new DoubleAnimation(srcW, dstW, duration) { EasingFunction = ease };
            var heightAnim = new DoubleAnimation(srcH, dstH, duration) { EasingFunction = ease };
            var opacityAnim = new DoubleAnimation(0.95, 0.0, duration)
            { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4) };

            opacityAnim.Completed += (_, _) => AnimationOverlay.Children.Remove(ghost);

            ghost.BeginAnimation(Canvas.LeftProperty, leftAnim);
            ghost.BeginAnimation(Canvas.TopProperty, topAnim);
            ghost.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
            ghost.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
            ghost.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }
        catch { }
    }

    // ----------------- GLOBAL HOTKEYS -----------------

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl shortcuts always handled
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.OemComma) { Settings_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
            if (e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
            if (e.Key == Key.Z) { Undo_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
            if (e.Key == Key.A)
            {
                ActiveSelector?.Items?.OfType<object>().ToList(); // ensure containers
                if (ActiveSelector is ListBox lb) { lb.SelectAll(); e.Handled = true; UpdateStats(); return; }
                if (ActiveSelector is ListView lv) { lv.SelectAll(); e.Handled = true; UpdateStats(); return; }
            }
        }

        // Special non-modifier shortcuts when not typing in a textbox
        if (Keyboard.FocusedElement is TextBox) return;

        if (e.Key == Key.F5) { Refresh_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.OemQuestion || (e.Key == Key.F1)) { Help_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.Delete) { Trash_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.N) { Skip_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.D0 || e.Key == Key.NumPad0) { ResetImageZoom(); e.Handled = true; return; }
        if (e.Key == Key.Space)
        {
            // Toggle video play/pause if current is video
            var idx = GetSelectedIndex();
            if (idx >= 0 && idx < MediaItems.Count && MediaItems[idx].Kind == MediaKind.Video)
            {
                VideoTogglePlay_Click(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        // Destination hotkeys
        foreach (var dest in Destinations)
        {
            if (dest.HotKey == Key.None) continue;
            if (dest.HotKey == e.Key && Keyboard.Modifiers == dest.Modifiers)
            {
                var items = GetSelectedItems();
                if (items.Count == 0) return;
                MoveItemsTo(items, dest, FindDestinationElement(dest));
                e.Handled = true;
                return;
            }
        }
    }

    // ----------------- SETTINGS / ABOUT / HELP -----------------

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings, Destinations) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            ThemeManager.ApplyOverride(_settings.ThemeOverride, _settings.AccentColor);
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

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new KeyboardHelpWindow { Owner = this };
        dlg.ShowDialog();
    }
}
