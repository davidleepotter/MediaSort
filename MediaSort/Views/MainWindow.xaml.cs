using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
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
    private bool _initializing = true; // suppress SaveSettings during startup so SelectionChanged etc. don't wipe the file before destinations are populated

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private DispatcherTimer? _videoTimer;
    // Debounces UpdatePreview during fast arrow-key scrolling so we don't decode
    // a full image/video for every intermediate selection. ~75 ms feels instant
    // when releasing the key but skips work while the key is held down.
    private DispatcherTimer? _previewDebounceTimer;
    private MediaItem? _pendingPreviewItem;
    private readonly MoveHistoryService _history = new();
    private CancellationTokenSource? _probeCts;

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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // PHASE 1 — fast UI-thread work only: load settings (small JSON file), populate
        // toolbar combos and destinations. This must finish quickly so the splash can
        // close and the window can be revealed.
        _settings = SettingsService.Load();

        RecursiveCheck.IsChecked = _settings.RecursiveScan;
        ViewModeCombo.SelectedIndex = (int)_settings.ViewMode;
        SortKeyCombo.SelectedIndex = (int)_settings.SortKey;
        DateFilterCombo.SelectedIndex = (int)_settings.DateFilter;
        AspectFilterCombo.SelectedIndex = (int)_settings.AspectGroupFilter;
        ActionCombo.SelectedIndex = (int)_settings.Action;
        UpdateSortDirButton();

        ThemeManager.ApplyOverride(_settings.ThemeOverride, _settings.AccentColor);

        CrashLogger.Info($"startup: loading {_settings.Destinations.Count} destinations from settings");
        foreach (var d in _settings.Destinations)
        {
            var btn = SettingsService.FromSerializable(d);
            Destinations.Add(btn);
            CrashLogger.Info($"startup: + dest '{btn.Name}' -> {btn.FolderPath}");
        }
        CrashLogger.Info($"startup: Destinations.Count after load = {Destinations.Count}");
        RefreshDestinationCounts();

        ApplyThumbnailSize();
        ApplyViewMode();
        Title = $"MediaSort v{VersionInfo.GetDisplayVersion()}";
        StatusText.Text = "Ready";

        // Done with initial UI population — allow SaveSettings to run again.
        _initializing = false;
        CrashLogger.Info("startup: _initializing = false (saves now enabled)");

        // PHASE 2 — slow stuff off the UI thread so the window can paint immediately.
        // (a) Initialize LibVLC on a background thread (loads native plugins from disk).
        // (b) Scan the source folder on a background thread (can be thousands of files).
        _ = InitializeVlcAsync();

        if (!string.IsNullOrWhiteSpace(_settings.SourceFolder) && Directory.Exists(_settings.SourceFolder))
        {
            await SetSourceFolderAsync(_settings.SourceFolder);
            PushRecentSource(_settings.SourceFolder);
            SaveSettings();
        }
    }

    /// <summary>Initializes LibVLC off the UI thread so it doesn't delay the first paint.</summary>
    private async Task InitializeVlcAsync()
    {
        try
        {
            var (libVlc, mediaPlayer) = await Task.Run(() =>
            {
                Core.Initialize();
                var lv = new LibVLC();
                var mp = new MediaPlayer(lv);
                return (lv, mp);
            });

            // Back on the UI thread — attach to the WPF VideoView and start the timer.
            _libVlc = libVlc;
            _mediaPlayer = mediaPlayer;
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
            CrashLogger.Log(ex, "vlc-init");
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        try { _videoTimer?.Stop(); } catch { }
        try { _mediaPlayer?.Stop(); } catch { }
        try { _mediaPlayer?.Dispose(); } catch { }
        try { _libVlc?.Dispose(); } catch { }

        SaveSettings();
    }

    private void SaveSettings([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (_initializing)
        {
            CrashLogger.Info($"SaveSettings SKIPPED (initializing) from {caller}");
            return;
        }
        CrashLogger.Info($"SaveSettings called from {caller} (Destinations.Count={Destinations.Count}, IsLoaded={IsLoaded})");
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

        // Capture FULL multi-selection (not just the primary item) so user selections
        // survive a re-sort triggered by probe completion / sort-key change.
        var previouslySelected = GetSelectedItems();
        var primary = ActiveSelector?.SelectedItem as MediaItem;

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

        // Skip the rebuild entirely if the order didn't change — avoids needless
        // container recycling and selection churn on every probe completion.
        bool orderChanged = sorted.Count != MediaItems.Count;
        if (!orderChanged)
        {
            for (int i = 0; i < sorted.Count; i++)
            {
                if (!ReferenceEquals(sorted[i], MediaItems[i])) { orderChanged = true; break; }
            }
        }
        if (!orderChanged) return;

        _suppressSelectionUpdate = true;
        MediaItems.Clear();
        foreach (var m in sorted) MediaItems.Add(m);
        _suppressSelectionUpdate = false;

        // Restore the full multi-selection.
        RestoreSelection(previouslySelected, primary);
        UpdatePositionDisplay();
    }

    /// <summary>Restores selection on all three list views after MediaItems was rebuilt.</summary>
    private void RestoreSelection(System.Collections.Generic.IList<MediaItem> items, MediaItem? primary)
    {
        if (items == null || items.Count == 0)
        {
            // Fall back to selecting the primary if multi-selection was empty.
            if (primary != null)
            {
                var idx = MediaItems.IndexOf(primary);
                if (idx >= 0) SelectIndex(idx);
            }
            return;
        }

        _suppressSelectionUpdate = true;
        try
        {
            ListView_List.SelectedItems.Clear();
            ListView_Details.SelectedItems.Clear();
            ListView_Thumbs.SelectedItems.Clear();
            foreach (var m in items)
            {
                if (!MediaItems.Contains(m)) continue;
                ListView_List.SelectedItems.Add(m);
                ListView_Details.SelectedItems.Add(m);
                ListView_Thumbs.SelectedItems.Add(m);
            }
        }
        finally
        {
            _suppressSelectionUpdate = false;
        }

        // Bring the primary back into view and refresh preview/stats.
        var anchor = primary != null && MediaItems.Contains(primary) ? primary : items[0];
        switch (ActiveSelector)
        {
            case ListBox lb when MediaItems.Contains(anchor):
                lb.ScrollIntoView(anchor);
                break;
            case ListView lv when MediaItems.Contains(anchor):
                lv.ScrollIntoView(anchor);
                break;
        }
        UpdatePreview(anchor);
        UpdateStats();
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

        // Toggle Select All <-> Unselect All so one button covers both directions.
        if (SelectAllButton != null)
        {
            bool everythingSelected = shown > 0 && sel >= shown;
            SelectAllButton.Content = everythingSelected ? "Unselect All" : "Select All";
            SelectAllButton.ToolTip = everythingSelected
                ? "Clear the current selection (Ctrl+A)"
                : "Select every visible item in the source list (Ctrl+A)";
        }
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
            PushRecentSource(dlg.SelectedPath);
            SaveSettings();
        }
    }

    // --- Recent source folders ---

    private const int RecentSourceMax = 10;

    /// <summary>
    /// Bumps a folder to the top of the recent list, dedupes (case-insensitive),
    /// and caps the list at RecentSourceMax entries. Caller is responsible for
    /// calling SaveSettings() when convenient.
    /// </summary>
    private void PushRecentSource(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        var list = _settings.RecentSourceFolders;
        // Remove any existing entry for this folder (case-insensitive on Windows paths).
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i], folder, StringComparison.OrdinalIgnoreCase))
                list.RemoveAt(i);
        }
        list.Insert(0, folder);
        while (list.Count > RecentSourceMax) list.RemoveAt(list.Count - 1);
    }

    private void RecentSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;

        var menu = new ContextMenu
        {
            PlacementTarget = btn,
            Placement = PlacementMode.Bottom,
        };

        var recents = _settings.RecentSourceFolders;
        if (recents.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "(no recent folders)",
                IsEnabled = false
            });
        }
        else
        {
            int i = 1;
            foreach (var path in recents.ToList()) // copy so handlers can mutate the list
            {
                var exists = Directory.Exists(path);
                var item = new MenuItem
                {
                    Header = $"_{i++}  {path}",
                    ToolTip = exists ? path : $"{path}  (missing)",
                    IsEnabled = exists
                };
                var captured = path;
                item.Click += (_, _) =>
                {
                    SetSourceFolder(captured);
                    _settings.SourceFolder = captured;
                    PushRecentSource(captured);
                    SaveSettings();
                    StatusText.Text = $"Source folder: {captured}";
                };
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());

            var clear = new MenuItem { Header = "Clear recent list" };
            clear.Click += (_, _) =>
            {
                _settings.RecentSourceFolders.Clear();
                SaveSettings();
                StatusText.Text = "Recent source folders cleared.";
            };
            menu.Items.Add(clear);

            // Prune entries whose folder no longer exists
            var pruneMissing = new MenuItem
            {
                Header = "Remove missing entries",
                IsEnabled = recents.Any(p => !Directory.Exists(p))
            };
            pruneMissing.Click += (_, _) =>
            {
                int before = _settings.RecentSourceFolders.Count;
                _settings.RecentSourceFolders.RemoveAll(p => !Directory.Exists(p));
                int removed = before - _settings.RecentSourceFolders.Count;
                SaveSettings();
                StatusText.Text = removed > 0
                    ? $"Removed {removed} missing folder(s) from Recent."
                    : "No missing folders to remove.";
            };
            menu.Items.Add(pruneMissing);
        }

        menu.IsOpen = true;
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

    /// <summary>Synchronous wrapper for callers (drag-drop, dialogs, command line) —
    /// fire-and-forget the async scan so the UI thread is never blocked.</summary>
    private void SetSourceFolder(string folder) => _ = SetSourceFolderAsync(folder);

    private async Task SetSourceFolderAsync(string folder)
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
        StatusText.Text = "Scanning…";

        bool recursive = RecursiveCheck.IsChecked == true;

        // Heavy folder enumeration on a background thread — huge folders can take seconds
        // and would otherwise freeze the UI right after launch.
        List<MediaItem> items;
        try
        {
            items = await Task.Run(() => MediaScanner.Scan(folder, recursive).ToList());
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"scan:{folder}");
            StatusText.Text = $"Scan failed: {ex.Message}";
            return;
        }

        _allItems.AddRange(items);
        ApplyFilter(); // also applies sort

        StatusText.Text = $"{_allItems.Count} media file(s) found";

        if (MediaItems.Count > 0) SelectIndex(0); else ClearPreview();

        // Background pass for thumbnails + dimensions
        StartBackgroundProbe(items, _probeCts.Token);
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

        // Thumbnail: read bytes + decode on the background thread. BitmapFromBytes
        // returns a frozen BitmapSource that's safe to hand to the UI thread.
        BitmapSource? thumb = null;
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
        SchedulePreview(MediaItems[index]);
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
                CrashLogger.Info($"select idx={idx} file={MediaItems[idx].FileName}");
                UpdatePositionDisplay();
                SchedulePreview(MediaItems[idx]);
                UpdateStats();
            }
            else
            {
                _pendingPreviewItem = null;
                _previewDebounceTimer?.Stop();
                ClearPreview();
                UpdateStats();
            }
        }
    }

    /// <summary>
    /// Coalesce rapid selection changes (e.g. holding the arrow key) into a single
    /// preview render. The first selection of a burst still feels instant because
    /// the timer only starts on the second hit — we render immediately if no
    /// debounce window is active.
    /// </summary>
    private void SchedulePreview(MediaItem item)
    {
        _pendingPreviewItem = item;
        if (_previewDebounceTimer == null)
        {
            _previewDebounceTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(75)
            };
            _previewDebounceTimer.Tick += (_, _) =>
            {
                _previewDebounceTimer?.Stop();
                if (_pendingPreviewItem != null) UpdatePreview(_pendingPreviewItem);
            };
        }

        // Restart the window: every fast key-press resets the 75 ms countdown.
        // Only the final settled selection actually triggers UpdatePreview.
        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Start();
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

    // Open a destination folder and make it the current source folder so the
    // user can sort within / out of an already-organized destination.
    private void UseDestAsSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DestinationButton dest) return;

        var folder = dest.FolderPath;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            MessageBox.Show($"Folder does not exist:\n\n{folder}", "Use as Source",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CrashLogger.Info($"use-dest-as-source: '{dest.Name}' -> {folder}");
        SetSourceFolder(folder);
        _settings.SourceFolder = folder;
        PushRecentSource(folder);
        SaveSettings();
        StatusText.Text = $"Source folder switched to '{dest.Name}' ({folder})";
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
            DispatchAction(items, dest, fe);
        }
    }

    // Single chokepoint for sending items to a destination, honoring the
    // toolbar Action dropdown: Move (default), Copy (keep originals), or
    // Delete (recycle originals without copying). Used by button click,
    // hotkeys, and any future trigger so the action setting is never bypassed.
    private void DispatchAction(List<MediaItem> items, DestinationButton dest, FrameworkElement? destinationElement)
    {
        switch (_settings.Action)
        {
            case FileAction.Copy:
                CopyItemsTo(items, dest, destinationElement);
                return;
            case FileAction.Delete:
                DeleteItemsFromDestinationButton(items);
                return;
            case FileAction.Move:
            default:
                MoveItemsTo(items, dest, destinationElement);
                return;
        }
    }

    private void ActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _settings == null) return;
        var idx = ActionCombo.SelectedIndex;
        if (idx < 0) return;
        _settings.Action = (FileAction)idx;
        StatusText.Text = $"Action mode: {_settings.Action}";
        SaveSettings();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        // Toggle behavior: if everything is already selected, clear the selection;
        // otherwise select all visible items.
        var sel = ActiveSelector;
        if (sel == null) return;
        bool everythingSelected = MediaItems.Count > 0 && sel.SelectedItems.Count >= MediaItems.Count;
        if (everythingSelected)
        {
            if (sel is ListBox lb) lb.UnselectAll();
            else if (sel is ListView lv) lv.UnselectAll();
        }
        else
        {
            if (sel is ListBox lb2) lb2.SelectAll();
            else if (sel is ListView lv2) lv2.SelectAll();
        }
        UpdateStats();
    }

    /// <summary>
    /// Copy variant of MoveItemsTo — same conflict handling, but originals stay in place
    /// and the source list is not modified.
    /// </summary>
    private void CopyItemsTo(List<MediaItem> items, DestinationButton dest, FrameworkElement? destinationElement)
    {
        if (string.IsNullOrWhiteSpace(dest.FolderPath))
        {
            StatusText.Text = $"Destination '{dest.Name}' has no folder set.";
            return;
        }

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

        // Reset per-batch conflict prefs
        _conflictPolicyForBatch = SettingPolicyToConflict(_settings.ConflictPolicy);
        _applyToAllForBatch = _conflictPolicyForBatch != ConflictPolicy.Prompt;

        int copied = 0;
        foreach (var item in items.ToList())
        {
            var rec = CopyWithPolicy(item.FullPath, dest, ref _conflictPolicyForBatch, ref _applyToAllForBatch);
            if (rec != null) copied++;
        }

        if (copied > 0)
        {
            StatusText.Text = $"Copied {copied} file(s) to {dest.Name}";
            RefreshDestinationCounts();
        }
        UpdateStats();
    }

    /// <summary>
    /// "Delete" mode: when a destination button is activated, ignore its folder
    /// and just send the selected source files to the Recycle Bin.
    /// </summary>
    private void DeleteItemsFromDestinationButton(List<MediaItem> items)
    {
        var msg = items.Count == 1
            ? $"Send '{items[0].FileName}' to the Recycle Bin?"
            : $"Send {items.Count} files to the Recycle Bin?";
        if (MessageBox.Show(msg, "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;

        StopVideo();
        PreviewImage.Source = null;

        var firstIdx = MediaItems.IndexOf(items[0]);
        var batch = new List<MoveHistoryService.MoveRecord>();
        var delSnapshot = items.ToList();
        int delTotal = delSnapshot.Count;
        int delProcessed = 0;
        foreach (var item in delSnapshot)
        {
            delProcessed++;
            if (delTotal > 1)
            {
                StatusText.Text = $"Deleting {delProcessed}/{delTotal}: {item.FileName}";
                try { Dispatcher.Invoke(() => { }, DispatcherPriority.Background); } catch { }
            }
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
            StatusText.Text = $"Deleted {batch.Count} file(s) (sent to Recycle Bin)";
        }

        if (MediaItems.Count > 0)
            SelectIndex(Math.Min(firstIdx, MediaItems.Count - 1));
        else { ClearPreview(); UpdatePositionDisplay(); }
        UpdateStats();
    }

    /// <summary>
    /// Copy variant of MoveWithPolicy — evaluates subfolder template, respects
    /// the per-batch conflict policy, and calls FileMover.CopyToFolder.
    /// </summary>
    private MoveHistoryService.MoveRecord? CopyWithPolicy(string sourcePath,
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

        var probable = Path.Combine(targetFolder, Path.GetFileName(sourcePath));
        if (File.Exists(probable) && policy == ConflictPolicy.Prompt && !applyAll)
        {
            var dlg = new ConflictDialog(Path.GetFileName(sourcePath), targetFolder) { Owner = this };
            if (dlg.ShowDialog() != true) return null;
            policy = dlg.Choice;
            applyAll = dlg.ApplyToAll;
            if (!applyAll)
            {
                var oneShot = policy;
                policy = ConflictPolicy.Prompt;
                return DoCopy(sourcePath, targetFolder, dest.RenameTemplate, oneShot);
            }
        }

        return DoCopy(sourcePath, targetFolder, dest.RenameTemplate, policy);
    }

    private MoveHistoryService.MoveRecord? DoCopy(string sourcePath, string targetFolder, string? rename, ConflictPolicy policy)
    {
        MoveResult r;
        // Mirror DoMove's large-file path: spin up a progress dialog with a real
        // progress bar + Cancel button when the source is >= 50 MB, so the UI
        // stays responsive on big copies (especially cross-volume).
        try
        {
            var fi = new System.IO.FileInfo(sourcePath);
            if (fi.Exists && fi.Length >= FileMoverProgress.LargeFileThreshold)
            {
                r = MoveOrCopyWithProgressDialog(sourcePath, targetFolder, policy, rename, isCopy: true);
            }
            else
            {
                r = FileMover.CopyToFolder(sourcePath, targetFolder, policy, rename);
            }
        }
        catch
        {
            r = FileMover.CopyToFolder(sourcePath, targetFolder, policy, rename);
        }

        if (r.Outcome == MoveOutcome.Moved)
        {
            return new MoveHistoryService.MoveRecord
            {
                OriginalPath = sourcePath,
                NewPath = r.FinalPath,
                When = DateTime.Now,
                Action = "Copy"
            };
        }
        if (r.Outcome == MoveOutcome.Failed)
        {
            StatusText.Text = $"Copy failed: {r.ErrorMessage}";
        }
        return null;
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

        // Animation — works for both single and multi-select.
        // Capture source positions/visuals BEFORE the move so containers still exist.
        var destElForAnim = destinationElement ?? FindDestinationElement(dest);
        TryPlayMoveAnimationsForBatch(items, destElForAnim);

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
            // Flash a transient "+N" badge on the destination so the user gets a clear
            // confirmation that the batch landed there, even if the fly-to animation
            // is short or off-screen.
            FlashDestinationBadge(dest, batch.Count);
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
        MoveResult r;
        // Large-file path: spin up a progress dialog and use the chunked mover so the
        // UI stays responsive and the user can cancel a 4 GB cross-volume copy.
        try
        {
            var fi = new System.IO.FileInfo(sourcePath);
            if (fi.Exists && fi.Length >= FileMoverProgress.LargeFileThreshold)
            {
                r = MoveOrCopyWithProgressDialog(sourcePath, targetFolder, policy, rename, isCopy: false);
            }
            else
            {
                r = FileMover.MoveToFolder(sourcePath, targetFolder, policy, rename);
            }
        }
        catch
        {
            r = FileMover.MoveToFolder(sourcePath, targetFolder, policy, rename);
        }

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

    /// <summary>
    /// Run a chunked move/copy on a worker thread while a modal ProgressDialog reports
    /// percent and supports cancel. Returns the final MoveResult.
    /// </summary>
    private MoveResult MoveOrCopyWithProgressDialog(string sourcePath, string targetFolder,
        ConflictPolicy policy, string? rename, bool isCopy)
    {
        var header = isCopy ? "Copying file…" : "Moving file…";
        var detail = System.IO.Path.GetFileName(sourcePath) + "  →  " + targetFolder;
        var dlg = new MediaSort.Views.ProgressDialog(header, detail) { Owner = this };

        MoveResult? captured = null;
        var progress = new System.Progress<(long done, long total)>(p => dlg.ReportProgress(p.done, p.total));
        var ct = dlg.Token;

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                captured = isCopy
                    ? FileMoverProgress.CopyWithProgress(sourcePath, targetFolder, policy, rename, progress, ct)
                    : FileMoverProgress.MoveWithProgress(sourcePath, targetFolder, policy, rename, progress, ct);
            }
            catch (System.Exception ex)
            {
                captured = new MoveResult { Outcome = MoveOutcome.Failed, ErrorMessage = ex.Message, OriginalPath = sourcePath };
            }
            finally
            {
                Dispatcher.BeginInvoke(new System.Action(() => { try { dlg.Close(); } catch { } }));
            }
        });
        thread.IsBackground = true;
        thread.Start();

        dlg.ShowDialog();
        thread.Join();
        return captured ?? new MoveResult { Outcome = MoveOutcome.Failed, ErrorMessage = "No result" };
    }

    // ----------------- UNDO / TRASH / SKIP -----------------

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var batch = _history.PopUndo();
        if (batch == null) { StatusText.Text = "Nothing to undo."; return; }

        int restored = 0;
        // Items + which destination they're flying back FROM, captured for the
        // reverse animation that plays after ApplyFilter realizes their containers.
        var restoredPairs = new List<(MediaItem item, FrameworkElement? sourceDestEl)>();

        foreach (var rec in batch)
        {
            if (rec.Action == "Move")
            {
                // Snapshot the destination element BEFORE the move so we can fly from it.
                var destEl = FindDestinationElementForPath(rec.NewPath);

                var r = FileMover.UndoMove(rec.NewPath, rec.OriginalPath);
                if (r.Outcome == MoveOutcome.Moved)
                {
                    var item = new MediaItem(r.FinalPath);
                    _allItems.Add(item);
                    restored++;
                    restoredPairs.Add((item, destEl));
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

        // Fire the reverse-flight animation after layout has rebuilt — otherwise the
        // item containers don't exist yet and TranslatePoint returns the wrong rect.
        if (restoredPairs.Count > 0)
        {
            Dispatcher.BeginInvoke(new Action(() => TryPlayRestoreAnimationsForBatch(restoredPairs)),
                                   System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Look up the destination button row whose folder path is the parent of <paramref name="filePath"/>.
    /// Returns the visual container so we can use its on-screen bounds as the animation origin.
    /// </summary>
    private FrameworkElement? FindDestinationElementForPath(string filePath)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(folder)) return null;
            var dest = Destinations.FirstOrDefault(d =>
                !string.IsNullOrEmpty(d.FolderPath) &&
                string.Equals(
                    System.IO.Path.GetFullPath(d.FolderPath).TrimEnd('\\', '/'),
                    System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase));
            return dest != null ? FindDestinationElement(dest) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Reverse of TryPlayMoveAnimationsForBatch: ghosts fly from each destination
    /// row back to the restored item's row in the source list.
    /// </summary>
    private void TryPlayRestoreAnimationsForBatch(List<(MediaItem item, FrameworkElement? destEl)> pairs)
    {
        if (pairs == null || pairs.Count == 0) return;

        int limit = Math.Min(pairs.Count, MaxAnimatedGhosts);
        // Fall back to the active selector itself when an individual item container
        // isn't realized (virtualized off-screen) — still gives the user a clear
        // "flying back to the source list" gesture.
        FrameworkElement? listFallback = ActiveSelector as FrameworkElement;

        for (int i = 0; i < limit; i++)
        {
            var (item, destEl) = pairs[i];
            if (destEl == null) continue;

            var targetEl = GetItemElement(item) ?? listFallback;
            if (targetEl == null) continue;

            var visual = CaptureItemVisual(item, targetEl);
            // Stagger ghosts so they fan in instead of stacking.
            var delayMs = i * 35;
            // Reuse TryPlayMoveAnimation with src=destEl, dest=item row — animation
            // direction is purely defined by the from/to elements.
            TryPlayMoveAnimation(item, destEl, targetEl, visual, delayMs);
        }
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
        var delSnapshot = items.ToList();
        int delTotal = delSnapshot.Count;
        int delProcessed = 0;
        foreach (var item in delSnapshot)
        {
            delProcessed++;
            if (delTotal > 1)
            {
                StatusText.Text = $"Deleting {delProcessed}/{delTotal}: {item.FileName}";
                try { Dispatcher.Invoke(() => { }, DispatcherPriority.Background); } catch { }
            }
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

    private FrameworkElement? GetItemElement(MediaItem item)
    {
        var selector = ActiveSelector;
        if (selector == null || item == null) return null;
        return selector.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
    }

    // Caps how many ghost copies fly at once when many files are selected
    // (visual clarity + perf). Extra items still move; they just don't animate.
    private const int MaxAnimatedGhosts = 12;

    private void TryPlayMoveAnimationsForBatch(List<MediaItem> items, FrameworkElement? destElement)
    {
        if (items == null || items.Count == 0) return;
        if (destElement == null) return;

        // Snapshot source element + visual NOW, before any items are removed from the list.
        // Limit to first N selected to keep the overlay readable when user selects 100 files.
        var snapshots = new List<(MediaItem item, FrameworkElement? src, ImageSource? visual)>();
        int limit = Math.Min(items.Count, MaxAnimatedGhosts);
        for (int i = 0; i < limit; i++)
        {
            var it = items[i];
            var srcEl = GetItemElement(it);
            // Fall back to currently-selected element when the item container isn't realized
            // (e.g. virtualized off-screen). Better to fly from selection than skip.
            if (srcEl == null && i == 0) srcEl = GetSelectedItemElement();
            var vis = CaptureItemVisual(it, srcEl);
            snapshots.Add((it, srcEl, vis));
        }

        for (int i = 0; i < snapshots.Count; i++)
        {
            var snap = snapshots[i];
            // Stagger ghosts so they fan out instead of stacking on a single point.
            var delayMs = i * 35;
            TryPlayMoveAnimation(snap.item, snap.src, destElement, snap.visual, delayMs);
        }
    }

    private FrameworkElement? FindDestinationElement(DestinationButton dest)
    {
        if (DestinationsPanel == null) return null;
        return DestinationsPanel.ItemContainerGenerator.ContainerFromItem(dest) as FrameworkElement;
    }

    /// <summary>
    /// Pulse a "+N" badge on the given destination so the user sees confirmation
    /// the batch landed. Sets DestinationButton.FlashBadge and animates FlashOpacity
    /// 0→1→0 over ~1.5s. Multiple rapid moves stack the count instead of fighting
    /// each other.
    /// </summary>
    private void FlashDestinationBadge(DestinationButton dest, int count)
    {
        if (dest == null || count <= 0) return;
        try
        {
            // Stack the count if a flash is already mid-animation.
            int existing = 0;
            if (!string.IsNullOrEmpty(dest.FlashBadge) &&
                dest.FlashBadge.StartsWith("+") &&
                int.TryParse(dest.FlashBadge.Substring(1), out var parsed))
            {
                existing = parsed;
            }
            dest.FlashBadge = $"+{existing + count}";
            dest.FlashOpacity = 0;

            // Use a DispatcherTimer-driven tween on the model property so the binding
            // pipes it straight to the overlay Border.Opacity. Avoids needing the
            // visual element handle.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            const double fadeInMs = 180;
            const double holdMs   = 900;
            const double fadeOutMs = 450;
            const double totalMs = fadeInMs + holdMs + fadeOutMs;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (s, e) =>
            {
                var t = sw.Elapsed.TotalMilliseconds;
                double op;
                if (t < fadeInMs)        op = t / fadeInMs;
                else if (t < fadeInMs + holdMs) op = 1.0;
                else if (t < totalMs)    op = 1.0 - ((t - fadeInMs - holdMs) / fadeOutMs);
                else                     op = 0.0;
                if (op < 0) op = 0; if (op > 1) op = 1;
                dest.FlashOpacity = op;
                if (t >= totalMs)
                {
                    timer.Stop();
                    dest.FlashOpacity = 0;
                    dest.FlashBadge = "";
                }
            };
            timer.Start();
        }
        catch { /* non-critical UI sugar */ }
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
                                      FrameworkElement? destElement,
                                      ImageSource? prebuiltVisual = null,
                                      int startDelayMs = 0)
    {
        try
        {
            if (AnimationOverlay == null) return;
            if (sourceElement == null || destElement == null) return;
            if (sourceElement.ActualWidth <= 0 || sourceElement.ActualHeight <= 0) return;
            if (destElement.ActualWidth <= 0 || destElement.ActualHeight <= 0) return;

            var visual = prebuiltVisual ?? CaptureItemVisual(item, sourceElement);
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
            var startDelay = startDelayMs > 0 ? TimeSpan.FromMilliseconds(startDelayMs) : (TimeSpan?)null;

            var leftAnim = new DoubleAnimation(srcTopLeft.X, dstTopLeft.X, duration) { EasingFunction = ease };
            var topAnim = new DoubleAnimation(srcTopLeft.Y, dstTopLeft.Y, duration) { EasingFunction = ease };
            var widthAnim = new DoubleAnimation(srcW, dstW, duration) { EasingFunction = ease };
            var heightAnim = new DoubleAnimation(srcH, dstH, duration) { EasingFunction = ease };
            var opacityAnim = new DoubleAnimation(0.95, 0.0, duration)
            { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(duration.TotalMilliseconds * 0.4) };

            if (startDelay.HasValue)
            {
                leftAnim.BeginTime = startDelay;
                topAnim.BeginTime = startDelay;
                widthAnim.BeginTime = startDelay;
                heightAnim.BeginTime = startDelay;
                opacityAnim.BeginTime = startDelay.Value + opacityAnim.BeginTime!.Value;
            }

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
                DispatchAction(items, dest, FindDestinationElement(dest));
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
            ApplyThumbnailSize();
            if (!string.IsNullOrWhiteSpace(_settings.SourceFolder)
                && _settings.SourceFolder != SourcePathText.Text)
            {
                SetSourceFolder(_settings.SourceFolder);
            }
            SaveSettings();
        }
    }

    /// <summary>
    /// Push the current AppSettings.ThumbnailSize into the Window resources so
    /// the Thumbnails view tiles resize live.
    /// </summary>
    private void ApplyThumbnailSize()
    {
        if (_settings == null) return;
        var size = Math.Max(60, Math.Min(240, _settings.ThumbnailSize));
        Resources["ThumbTileSize"] = (double)size;
        Resources["ThumbTileHeight"] = (double)(size + 20);
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

    private void SaveDebugLog_Click(object sender, RoutedEventArgs e) => SaveDebugLog();

    internal void SaveDebugLog()
    {
        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Debug Log",
            FileName = $"mediasort-debug-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExt = ".txt",
            Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*"
        };
        if (sfd.ShowDialog() != true) return;

        try
        {
            using var sw = new StreamWriter(sfd.FileName, false);

            sw.WriteLine("=== MediaSort Debug Log ===");
            sw.WriteLine($"Captured:        {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sw.WriteLine($"Version:         {VersionInfo.GetVersion()}");
            sw.WriteLine($"OS:              {Environment.OSVersion}");
            sw.WriteLine($".NET runtime:    {Environment.Version}");
            sw.WriteLine($"Process bits:    {(Environment.Is64BitProcess ? "64" : "32")}-bit");
            sw.WriteLine($"Working set:     {Environment.WorkingSet / (1024 * 1024)} MB");
            sw.WriteLine();

            sw.WriteLine("=== State ===");
            sw.WriteLine($"Source folder:   {_settings?.SourceFolder ?? "(none)"}");
            sw.WriteLine($"Recursive:       {_settings?.RecursiveScan}");
            sw.WriteLine($"View mode:       {_settings?.ViewMode}");
            sw.WriteLine($"Sort key:        {_settings?.SortKey}");
            sw.WriteLine($"Theme override:  {_settings?.ThemeOverride}");
            sw.WriteLine($"All items:       {_allItems.Count}");
            sw.WriteLine($"Visible items:   {MediaItems.Count}");
            sw.WriteLine($"Destinations:    {Destinations.Count}");
            foreach (var d in Destinations)
            {
                sw.WriteLine($"  - {d.Name} -> {d.FolderPath}  hotkey={d.HotKeyDisplay}  kind={d.KindFilter}");
            }
            sw.WriteLine();

            sw.WriteLine("=== Settings file ===");
            sw.WriteLine($"Path: {SettingsService.SettingsFilePath}");
            try
            {
                if (File.Exists(SettingsService.SettingsFilePath))
                {
                    var fi = new FileInfo(SettingsService.SettingsFilePath);
                    sw.WriteLine($"Exists: yes  size={fi.Length} bytes  modified={fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    sw.WriteLine("--- contents ---");
                    sw.Write(File.ReadAllText(SettingsService.SettingsFilePath));
                    sw.WriteLine();
                    sw.WriteLine("--- end contents ---");
                }
                else
                {
                    sw.WriteLine("Exists: no");
                }
            }
            catch (Exception ex)
            {
                sw.WriteLine($"(could not read settings.json: {ex.Message})");
            }
            sw.WriteLine();

            sw.WriteLine("=== Items (first 50) ===");
            foreach (var m in _allItems.Take(50))
            {
                sw.WriteLine($"  [{m.Kind}] {m.FileName}  size={m.SizeBytes}  dims={m.PixelWidth}x{m.PixelHeight}  thumb={(m.Thumbnail != null ? "YES" : "no")}");
            }
            if (_allItems.Count > 50) sw.WriteLine($"  ... and {_allItems.Count - 50} more");
            sw.WriteLine();

            sw.WriteLine("=== Crash log (%LOCALAPPDATA%\\MediaSort\\crash.log) ===");
            try
            {
                if (File.Exists(CrashLogger.LogFilePath))
                {
                    sw.WriteLine($"(from {CrashLogger.LogFilePath})");
                    sw.WriteLine();
                    sw.Write(File.ReadAllText(CrashLogger.LogFilePath));
                }
                else
                {
                    sw.WriteLine("(no crash.log found yet)");
                }
            }
            catch (Exception ex)
            {
                sw.WriteLine($"(could not read crash.log: {ex.Message})");
            }

            StatusText.Text = $"Debug log saved to {sfd.FileName}";
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{sfd.FileName}\""); } catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save debug log:\n\n{ex.Message}", "Save Debug Log",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
