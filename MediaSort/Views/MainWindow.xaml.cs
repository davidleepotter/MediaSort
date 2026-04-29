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
    // Separate CTS so we can cancel an in-flight folder enumeration when the user
    // picks a new source folder mid-scan (huge folders / network shares).
    private CancellationTokenSource? _scanCts;

    // ---- Probe batching infrastructure ----
    // The naive approach (one Dispatcher.BeginInvoke per probed file for dimensions
    // AND another for the thumbnail) flooded the UI dispatcher with 5,000+ posts on
    // a 1,938-image scan. Each post triggers PropertyChanged → WPF re-measure of any
    // realized container bound to that item, which on the non-virtualizing WrapPanel
    // (Thumbs view) means re-measuring every realized tile. That's why the user saw
    // mouse sluggishness despite CPU at 6%: the dispatcher was the bottleneck.
    //
    // Now we accumulate per-item updates in a thread-safe queue and a single timer
    // on the UI thread drains it every 150 ms, applying all pending updates in one
    // batch. Drops dispatcher posts from O(n_files) to O(scan_duration / 150ms).
    private readonly System.Collections.Concurrent.ConcurrentQueue<ProbeUpdate> _probeUpdates = new();
    private DispatcherTimer? _probeFlushTimer;

    private readonly struct ProbeUpdate
    {
        public readonly MediaItem Item;
        public readonly int Width;
        public readonly int Height;
        public readonly double DurationSeconds;
        public readonly BitmapSource? Thumbnail;
        public ProbeUpdate(MediaItem item, int w, int h, double dur, BitmapSource? thumb)
        { Item = item; Width = w; Height = h; DurationSeconds = dur; Thumbnail = thumb; }
    }
    // (#8) Last destination dispatched to, so the user can repeat-fire it with `.`
    // without lifting their hand from the keyboard.
    private DestinationButton? _lastDestination;

    // (#9) Multi-destination split: while Shift is held, destination hotkeys queue
    // up instead of firing immediately. On Shift release the queued list is dispatched
    // — last entry uses the toolbar Action (typically Move), earlier entries Copy.
    private readonly List<DestinationButton> _destQueue = new();
    private List<MediaItem>? _destQueueItems; // captured at queue-start time

    // (#16) Volume monitor: detects USB / network unmount and online recovery.
    private VolumeMonitor? _volumeMonitor;

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
        SourceInitialized += (_, _) =>
        {
            WindowChrome.ApplyCurrentTheme(this);
            // (#16) Hook WM_DEVICECHANGE so we react to USB plug/unplug instantly.
            _volumeMonitor = new VolumeMonitor();
            _volumeMonitor.StatusChanged += VolumeMonitor_StatusChanged;
            _volumeMonitor.Attach(this);
            RefreshVolumeWatchList();
        };
    }

    // ===================== (#16) USB / NETWORK UNMOUNT DETECTION =====================

    /// <summary>
    /// Re-sync the VolumeMonitor's watch list from current source folder + destinations.
    /// Call after any change to either set.
    /// </summary>
    private void RefreshVolumeWatchList()
    {
        if (_volumeMonitor == null || _settings == null) return;
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(_settings.SourceFolder)) paths.Add(_settings.SourceFolder);
        foreach (var d in Destinations)
            if (!string.IsNullOrWhiteSpace(d.FolderPath)) paths.Add(d.FolderPath);
        _volumeMonitor.SetWatchList(paths);
        // Push current state into UI immediately so freshly-added destinations
        // show their banner without waiting for the first transition event.
        UpdateOfflineBindings();
    }

    private void VolumeMonitor_StatusChanged(VolumeStatusChange change)
    {
        // Marshal to UI thread (HwndSource hook can fire on the UI thread already, but
        // belt-and-braces for the timer poll path).
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => VolumeMonitor_StatusChanged(change)));
            return;
        }
        UpdateOfflineBindings();
        if (_settings != null && string.Equals(change.Path, _settings.SourceFolder, StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = change.IsOnline
                ? $"Source folder reconnected: {change.Path}"
                : $"Source folder went offline: {change.Path}";
        }
        else
        {
            StatusText.Text = change.IsOnline
                ? $"Destination reconnected: {change.Path}"
                : $"Destination went offline: {change.Path}";
        }
    }

    private void UpdateOfflineBindings()
    {
        if (_volumeMonitor == null) return;
        // Source banner
        if (_settings != null && !string.IsNullOrWhiteSpace(_settings.SourceFolder))
        {
            bool online = _volumeMonitor.IsOnline(_settings.SourceFolder);
            SourceOfflineBanner.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        }
        else
        {
            SourceOfflineBanner.Visibility = Visibility.Collapsed;
        }
        // Destination flags
        foreach (var d in Destinations)
        {
            if (string.IsNullOrWhiteSpace(d.FolderPath)) { d.IsOffline = false; continue; }
            d.IsOffline = !_volumeMonitor.IsOnline(d.FolderPath);
        }
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

        // (#20) Restore audio preview state. Slider .Value / ToggleButton .IsChecked
        // sets only fire ValueChanged once after _initializing flips to false, so we
        // do not need to suppress them here.
        VolumeSlider.Value = Math.Max(0, Math.Min(100, _settings.VideoVolume));
        MuteToggle.IsChecked = _settings.VideoMuted;
        MuteToggle.Content = _settings.VideoMuted ? "\ud83d\udd07" : "\ud83d\udd0a";

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

        // Restore persisted splitter widths. 0 = use the default from XAML.
        // Clamp to MinWidth so a previously narrow window can't lock us out.
        if (_settings.LeftPanelWidth > 0 && LeftPanelColumn != null)
        {
            var w = Math.Max(_settings.LeftPanelWidth, LeftPanelColumn.MinWidth);
            LeftPanelColumn.Width = new GridLength(w, GridUnitType.Pixel);
        }
        if (_settings.RightPanelWidth > 0 && RightPanelColumn != null)
        {
            var w = Math.Max(_settings.RightPanelWidth, RightPanelColumn.MinWidth);
            RightPanelColumn.Width = new GridLength(w, GridUnitType.Pixel);
        }

        // Done with initial UI population — allow SaveSettings to run again.
        _initializing = false;
        CrashLogger.Info("startup: _initializing = false (saves now enabled)");

        // PHASE 2 — slow stuff off the UI thread so the window can paint immediately.
        // (#4) LibVLC is no longer eagerly initialized at startup. Loading the native
        // plugins is the single largest contributor to cold-start time (~300-800 ms
        // depending on disk). We now defer it until the user previews their first
        // video, via EnsureVlcInitializedAsync(). The folder scan still runs now.

        if (!string.IsNullOrWhiteSpace(_settings.SourceFolder) && Directory.Exists(_settings.SourceFolder))
        {
            await SetSourceFolderAsync(_settings.SourceFolder);
            PushRecentSource(_settings.SourceFolder);
            SaveSettings();
        }
    }

    // (#4) Lazy LibVLC init. The single Task is cached so concurrent video previews
    // share one initialization. Returns true on success, false if init failed.
    private Task<bool>? _vlcInitTask;

    /// <summary>
    /// (#4) Triggered by UpdatePreview for a video item. If LibVLC isn't ready yet,
    /// kicks off (or awaits the in-flight) lazy init and shows a brief "Initializing
    /// video player…" status. Once ready, plays the media. If the user clicks away
    /// to a different item before init completes, the new selection wins — we check
    /// _pendingPreviewItem (or current selection) before calling Play.
    /// </summary>
    private async Task StartVideoPreviewAsync(MediaItem item)
    {
        // Optimistic UI: show the controls bar; if init fails we'll hide them again.
        PreviewVideo.Visibility = Visibility.Visible;
        VideoControls.Visibility = Visibility.Visible;
        PreviewEmpty.Visibility = Visibility.Collapsed;

        bool needInit = _vlcInitTask == null && (_mediaPlayer == null || _libVlc == null);
        if (needInit)
            StatusText.Text = "Initializing video player…";

        bool ok = await EnsureVlcInitializedAsync();
        if (!ok || _mediaPlayer == null || _libVlc == null)
        {
            PreviewEmpty.Text = "Video playback unavailable (LibVLC failed to initialize).";
            PreviewEmpty.Visibility = Visibility.Visible;
            PreviewVideo.Visibility = Visibility.Collapsed;
            VideoControls.Visibility = Visibility.Collapsed;
            return;
        }

        // Bail out if the user has navigated away while LibVLC was loading.
        var currentSel = GetSelectedItems().FirstOrDefault();
        if (currentSel != null && !ReferenceEquals(currentSel, item)) return;
        if (needInit) StatusText.Text = "Ready";

        try
        {
            // Don't call _mediaPlayer.Stop() here — LibVLCSharp's Play(newMedia) replaces
            // the current media internally, and Stop() is synchronous and can block.
            try { _videoTimer?.Stop(); } catch { }
            using var media = new Media(_libVlc, new Uri(item.FullPath));
            _mediaPlayer.Play(media);
            ApplyAudioPreviewState(); // (#20) carry mute / volume into the new media
            _videoTimer?.Start();

            PopulateExif(item);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Preview error: {ex.Message}";
            PreviewEmpty.Text = $"Preview error: {ex.Message}";
            PreviewEmpty.Visibility = Visibility.Visible;
            PreviewVideo.Visibility = Visibility.Collapsed;
            VideoControls.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// (#4) Ensures LibVLC + MediaPlayer + video timer are ready. Safe to call repeatedly
    /// — the work runs at most once per session. Returns true on success.
    /// </summary>
    private Task<bool> EnsureVlcInitializedAsync()
    {
        if (_vlcInitTask != null) return _vlcInitTask;
        _vlcInitTask = InitializeVlcAsync();
        return _vlcInitTask;
    }

    /// <summary>Initializes LibVLC off the UI thread so it doesn't delay the first paint.</summary>
    private async Task<bool> InitializeVlcAsync()
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
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Video playback unavailable: {ex.Message}";
            CrashLogger.Log(ex, "vlc-init");
            return false;
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
        // (#19) Persist per-folder view/sort so each source folder remembers how the user likes to look at it.
        SaveFolderStateForCurrent();
        SettingsService.Save(_settings);
    }

    // ----------------- PER-FOLDER STATE (#19) -----------------

    /// <summary>
    /// Normalize a folder path so we get a single canonical key per folder regardless
    /// of trailing slashes or case differences on Windows.
    /// </summary>
    private static string NormalizeFolderKey(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";
        try
        {
            return System.IO.Path.GetFullPath(folder)
                .TrimEnd('\\', '/')
                .ToLowerInvariant();
        }
        catch { return folder.ToLowerInvariant(); }
    }

    /// <summary>
    /// If we have a saved view/sort for this folder, push it into _settings (and the combos)
    /// so the next ApplyFilter / ApplyViewMode picks it up.
    /// </summary>
    private void ApplyFolderState(string folder)
    {
        var key = NormalizeFolderKey(folder);
        if (string.IsNullOrEmpty(key)) return;
        var state = _settings.FolderStates.FirstOrDefault(s => NormalizeFolderKey(s.Path) == key);
        if (state == null) return;

        _settings.ViewMode = state.ViewMode;
        _settings.SortKey = state.SortKey;
        _settings.SortDescending = state.SortDescending;

        // Reflect in UI without triggering SaveSettings recursion (combos fire SelectionChanged
        // but the _initializing path is gone by now — SaveSettings is harmless because we're
        // about to save anyway).
        if (ViewModeCombo != null && ViewModeCombo.SelectedIndex != (int)state.ViewMode)
            ViewModeCombo.SelectedIndex = (int)state.ViewMode;
        if (SortKeyCombo != null && SortKeyCombo.SelectedIndex != (int)state.SortKey)
            SortKeyCombo.SelectedIndex = (int)state.SortKey;
        UpdateSortDirButton();
    }

    /// <summary>Persist the current view/sort under the current source folder key.</summary>
    private void SaveFolderStateForCurrent()
    {
        var folder = _settings.SourceFolder;
        var key = NormalizeFolderKey(folder);
        if (string.IsNullOrEmpty(key)) return;

        var state = _settings.FolderStates.FirstOrDefault(s => NormalizeFolderKey(s.Path) == key);
        if (state == null)
        {
            state = new PerFolderState { Path = folder };
            _settings.FolderStates.Add(state);
        }
        state.ViewMode = _settings.ViewMode;
        state.SortKey = _settings.SortKey;
        state.SortDescending = _settings.SortDescending;

        // Cap so the JSON doesn't grow unbounded across years of usage.
        const int MaxFolderStates = 200;
        if (_settings.FolderStates.Count > MaxFolderStates)
        {
            _settings.FolderStates.RemoveRange(0, _settings.FolderStates.Count - MaxFolderStates);
        }
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
        StartBackgroundProbe(_allItems.ToList(), _probeCts.Token, reportCompletion: true);
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

        // Cancel a still-running enumeration from a previous folder pick (#23).
        try { _scanCts?.Cancel(); } catch { }
        _scanCts = new CancellationTokenSource();
        var scanToken = _scanCts.Token;

        // Stop any video that is still playing from the previous selection so
        // LibVLC isn't holding a file handle while we switch folders.
        StopVideo();

        SourcePathText.Text = folder;
        _allItems.Clear();
        MediaItems.Clear();
        StatusText.Text = "Scanning…";

        // (#16) Track the new source path for unmount detection.
        RefreshVolumeWatchList();

        // (#19) If we have remembered view/sort prefs for this folder, apply them BEFORE
        // ApplyFilter so the new items render the way the user last left them.
        ApplyFolderState(folder);
        ApplyViewMode();

        bool recursive = RecursiveCheck.IsChecked == true;
        bool includeHidden = _settings.IncludeHiddenFiles;

        // Try to set up a progress popup. We tolerate ANY failure (e.g. the main
        // window isn't shown yet during initial startup, which would make Owner=
        // this throw) by simply skipping the popup — scan continues normally.
        //
        // UX rules:
        //   1. Don't show at all if the scan finishes before 80 ms (avoid noise on
        //      already-cached folders — most quick scans are well under 80 ms).
        //   2. Once shown, stay visible for at least 600 ms so the user can read
        //      it (otherwise it just flashes when scans take ~300 ms).
        ProgressDialog? scanDialog = null;
        DispatcherTimer? revealTimer = null;
        DateTime dialogShownAt = DateTime.MinValue;
        const int RevealDelayMs = 80;
        const int MinVisibleMs = 600;
        // We register a callback on the dialog's cancel token to cancel the scan.
        // The registration MUST be disposed before we Close() the dialog ourselves,
        // because ProgressDialog.OnClosed cancels its own token — without disposing,
        // our normal cleanup would erroneously cancel the just-completed scan and
        // make the result list look invalid (this caused the v1.0.77+ regression
        // where the source list was empty at startup).
        CancellationTokenRegistration scanCancelReg = default;
        try
        {
            // Owner is only safe to set if the main window has actually been shown.
            bool canOwn = this.IsVisible;
            scanDialog = new ProgressDialog("Scanning folder…", folder)
            {
                ShowInTaskbar = false,
            };
            if (canOwn) scanDialog.Owner = this;
            scanDialog.ReportProgress(0, 0); // indeterminate bar
            var dlgRef = scanDialog;
            scanCancelReg = scanDialog.Token.Register(() =>
            {
                try { _scanCts?.Cancel(); } catch { }
            });

            // DispatcherTimer reveals the dialog after 250ms so quick scans don't
            // flash a popup. Cleanup is unconditional in the outer `finally`.
            revealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RevealDelayMs) };
            revealTimer.Tick += (_, _) =>
            {
                revealTimer!.Stop();
                if (scanToken.IsCancellationRequested) return;
                try
                {
                    dlgRef.Show();
                    dialogShownAt = DateTime.UtcNow;
                }
                catch (Exception ex) { CrashLogger.Log(ex, "scan-popup-show"); }
            };
            revealTimer.Start();
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "scan-popup-init");
            scanDialog = null;
            revealTimer = null;
        }

        // Heavy folder enumeration on a background thread — huge folders can take seconds
        // and would otherwise freeze the UI right after launch. Now cancellation-aware (#23),
        // hidden/system-aware (#24), and progress-aware via the popup above.
        List<MediaItem> items;
        try
        {
            items = await Task.Run(() =>
            {
                var list = new List<MediaItem>();
                int images = 0, videos = 0;
                var nextTick = Environment.TickCount + 100;
                foreach (var mi in MediaScanner.Scan(folder, recursive, includeHidden, scanToken))
                {
                    list.Add(mi);
                    if (mi.Kind == MediaKind.Image) images++; else if (mi.Kind == MediaKind.Video) videos++;
                    if (Environment.TickCount >= nextTick)
                    {
                        nextTick = Environment.TickCount + 100;
                        var imgN = images; var vidN = videos;
                        var dlgCap = scanDialog;
                        if (dlgCap != null)
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                try { dlgCap.SetDetail($"Found {imgN:N0} image(s), {vidN:N0} video(s)…"); }
                                catch { /* dialog closed */ }
                            }));
                        }
                    }
                }
                return list;
            }, scanToken);
        }
        catch (OperationCanceledException)
        {
            // A newer scan started — silently abandon this one. The newer call already
            // cleared collections and updated status text.
            return;
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"scan:{folder}");
            StatusText.Text = $"Scan failed: {ex.Message}";
            return;
        }
        finally
        {
            try { revealTimer?.Stop(); } catch { }
            // CRITICAL: dispose the cancel registration BEFORE the dialog might be
            // closed. Otherwise ProgressDialog.OnClosed -> its CTS.Cancel() fires
            // our registered callback which cancels _scanCts. (We re-register below
            // for the probe phase against the probe CTS instead.)
            try { scanCancelReg.Dispose(); } catch { }
            // NOTE: we deliberately DO NOT close scanDialog here anymore — on a
            // network share the slow phase is the probe (thumbnail decoding), not
            // enumeration. Keeping the popup open through probe gives the user the
            // visual feedback they asked for. StartBackgroundProbe owns it from here.
        }

        // If we were cancelled while the result list was being materialized, bail.
        if (scanToken.IsCancellationRequested)
        {
            try { scanDialog?.Close(); } catch { }
            return;
        }

        // (#11) Re-apply favorites flag from persisted set.
        if (_settings.Favorites.Count > 0)
        {
            var favSet = new HashSet<string>(_settings.Favorites, StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (favSet.Contains(it.FullPath)) it.IsFavorite = true;
            }
        }

        _allItems.AddRange(items);
        ApplyFilter(); // also applies sort

        StatusText.Text = $"{_allItems.Count} media file(s) found";

        if (MediaItems.Count > 0) SelectIndex(0); else ClearPreview();

        // Hand the popup to the probe phase. It will switch the bar to determinate
        // (X of Y), update detail text per file, and close the dialog when probe
        // completes (or the user cancels). If no items were found, close immediately.
        if (items.Count == 0)
        {
            try { scanDialog?.Close(); } catch { }
            return;
        }

        // Background pass for thumbnails + dimensions — owns scanDialog from here.
        StartBackgroundProbe(items, _probeCts.Token, reportCompletion: false,
                             progressDialog: scanDialog,
                             dialogShownAt: dialogShownAt,
                             minVisibleMs: MinVisibleMs);
    }

    /// <summary>Queue a probe update for batched application on the UI thread.
    /// Safe to call from any thread. Pass 0/null for fields you don't want to update.</summary>
    private void EnqueueProbeUpdate(MediaItem item, int w, int h, double dur, BitmapSource? thumb)
    {
        _probeUpdates.Enqueue(new ProbeUpdate(item, w, h, dur, thumb));
    }

    /// <summary>Start the UI-thread flush timer that drains _probeUpdates every 150ms.
    /// Idempotent — if already running, does nothing.</summary>
    private void StartProbeFlushTimer()
    {
        if (_probeFlushTimer != null) return;
        _probeFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _probeFlushTimer.Tick += (_, _) => FlushProbeUpdates();
        _probeFlushTimer.Start();
    }

    /// <summary>Stop the flush timer and drain any remaining updates one last time.</summary>
    private void StopProbeFlushTimer()
    {
        try { _probeFlushTimer?.Stop(); } catch { }
        _probeFlushTimer = null;
        // Drain anything still queued so the user sees the last few thumbnails.
        FlushProbeUpdates();
    }

    /// <summary>Apply all queued probe updates in a single UI-thread pass.
    /// Each ProbeUpdate triggers up to 4 PropertyChanged events on its MediaItem;
    /// applying them in a tight loop is far cheaper than crossing the dispatcher
    /// boundary for each update.</summary>
    private void FlushProbeUpdates()
    {
        // Cap drains per tick so a giant backlog doesn't freeze the UI in one go.
        // 200 items per 150ms = 1,333 items/sec applied — enough to keep up with any
        // realistic probe rate, while leaving the dispatcher responsive between ticks.
        const int MaxPerTick = 200;
        int n = 0;
        while (n < MaxPerTick && _probeUpdates.TryDequeue(out var u))
        {
            try
            {
                if (u.Width > 0 && u.Height > 0)
                {
                    u.Item.PixelWidth = u.Width;
                    u.Item.PixelHeight = u.Height;
                }
                if (u.DurationSeconds > 0)
                {
                    u.Item.DurationSeconds = u.DurationSeconds;
                }
                if (u.Thumbnail != null)
                {
                    u.Item.Thumbnail = u.Thumbnail;
                }
            }
            catch (Exception ex) { CrashLogger.Log(ex, "flush-probe-update"); }
            n++;
        }
    }

    private void StartBackgroundProbe(List<MediaItem> items, CancellationToken ct, bool reportCompletion = false,
                                      ProgressDialog? progressDialog = null,
                                      DateTime dialogShownAt = default,
                                      int minVisibleMs = 0)
    {
        var imageCount = items.Count(i => i.Kind == MediaKind.Image);
        var videoCount = items.Count(i => i.Kind == MediaKind.Video);
        CrashLogger.Info($"probe:start total={items.Count} images={imageCount} videos={videoCount} report={reportCompletion}");

        int totalForStatus = items.Count;

        // Detect UNC / network-share source. We use this to lower probe parallelism
        // to 1 (SMB redirector serializes anyway, and 2 concurrent decoders cause WIC
        // to block the dispatcher pump on slow shares).
        string? samplePath = items.Count > 0 ? items[0].FullPath : null;
        bool isNetworkSource = false;
        try
        {
            if (!string.IsNullOrEmpty(samplePath))
            {
                if (samplePath.StartsWith(@"\\", StringComparison.Ordinal)) isNetworkSource = true;
                else if (samplePath.Length >= 2 && samplePath[1] == ':')
                {
                    var di = new System.IO.DriveInfo(samplePath.Substring(0, 2) + "\\");
                    if (di.DriveType == System.IO.DriveType.Network) isNetworkSource = true;
                }
            }
        }
        catch { }
        CrashLogger.Info($"probe:source-type network={isNetworkSource} sample={samplePath}");

        // Start the batched UI-flush timer for the duration of this probe pass.
        // Always start on the UI thread (we are on it here — StartBackgroundProbe is
        // called from the dispatcher).
        StartProbeFlushTimer();

        // Wire the popup's Cancel button to the probe CTS. Caller (SetSourceFolderAsync)
        // already disposed its own scan-CTS registration; this one cancels probe.
        CancellationTokenRegistration probeCancelReg = default;
        DateTime probeDialogShownAt = dialogShownAt;
        if (progressDialog != null)
        {
            try
            {
                progressDialog.SetDetail(items.Count == 1
                    ? "Loading thumbnail for 1 file…"
                    : $"Loading thumbnails for {items.Count:N0} files…");
                // Switch to determinate progress (we now know total).
                progressDialog.ReportProgress(0, items.Count);
                // The reveal timer in SetSourceFolderAsync is stopped by the time we get
                // here. If enumeration was faster than the 80ms reveal delay, the dialog
                // was never shown — but we still need it visible during probe (which is
                // the slow phase on network shares). Show it now if not already visible.
                if (!progressDialog.IsVisible)
                {
                    try
                    {
                        progressDialog.Show();
                        probeDialogShownAt = DateTime.UtcNow;
                    }
                    catch (Exception ex) { CrashLogger.Log(ex, "probe:dialog-show"); }
                }
                probeCancelReg = progressDialog.Token.Register(() =>
                {
                    try { _probeCts?.Cancel(); } catch { }
                });
            }
            catch (Exception ex) { CrashLogger.Log(ex, "probe:dialog-init"); }
        }

        _ = Task.Run(() =>
        {
            // Lower the priority of the entire probe pipeline so thumbnail decoding
            // never starves UI input (mouse, scroll, keyboard). User reported very
            // sluggish mouse movement on a 1938-image network share — BelowNormal
            // pool threads still complete the work, just behind the foreground UI.
            try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }

            // (#1) Parallelize image thumbnails. Cap parallelism so we don't drown the
            // disk on slow drives or thrash the dispatcher with thousands of cross-thread
            // BeginInvoke calls. Videos stay serial — LibVLC's media-parse path is not safe
            // to fan out across threads.
            int thumbsAssigned = 0;
            int dimsAssigned = 0;
            int failures = 0;
            int processed = 0;

            var images = items.Where(i => i.Kind == MediaKind.Image).ToList();
            var videos = items.Where(i => i.Kind == MediaKind.Video).ToList();

            // On UNC / mapped network drives, force DOP=1. The SMB redirector serializes
            // reads at the kernel level anyway, and concurrent BitmapImage decodes both
            // hold CPU + I/O completion ports, which is what was making the user's mouse
            // sluggish during scan. On local disk, 2 in flight is fine.
            int dop = isNetworkSource
                ? 1
                : Math.Min(2, Math.Max(1, Environment.ProcessorCount / 4));
            CrashLogger.Info($"probe:dop={dop} (network={isNetworkSource})");

            // Periodic status update. Only post on count change AND minimum 200ms
            // gap so we don't spam the dispatcher — each post itself contends with
            // the UI thread.
            int lastStatusTick = Environment.TickCount;
            int lastStatusValue = -1;
            void PostStatus(int done, bool force)
            {
                if (totalForStatus <= 0) return;
                int now = Environment.TickCount;
                if (!force && (now - lastStatusTick) < 200) return;
                if (!force && done == lastStatusValue) return;
                lastStatusTick = now;
                lastStatusValue = done;
                int doneCap = done;
                int totalCap = totalForStatus;
                var dlgCap = progressDialog;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    StatusText.Text = $"Loading thumbnails… {doneCap:N0} of {totalCap:N0}";
                    if (dlgCap != null)
                    {
                        try
                        {
                            dlgCap.ReportProgress(doneCap, totalCap);
                            dlgCap.SetDetail($"Loading thumbnails… {doneCap:N0} of {totalCap:N0}");
                        }
                        catch { /* dialog closed */ }
                    }
                }));
            }

            try
            {
                Parallel.ForEach(
                    images,
                    new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
                    item =>
                    {
                        if (ct.IsCancellationRequested) return;
                        try { System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal; } catch { }
                        try
                        {
                            if (ProbeImage(item, ct))
                                System.Threading.Interlocked.Increment(ref thumbsAssigned);
                            if (item.PixelWidth > 0)
                                System.Threading.Interlocked.Increment(ref dimsAssigned);
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex)
                        {
                            System.Threading.Interlocked.Increment(ref failures);
                            CrashLogger.Log(ex, $"probe:{item.FullPath}");
                        }
                        int p = System.Threading.Interlocked.Increment(ref processed);
                        PostStatus(p, force: false);
                    });
            }
            catch (OperationCanceledException)
            {
                CrashLogger.Info("probe:cancelled");
                CloseProgressDialog();
                return;
            }

            // Videos serially.
            foreach (var item in videos)
            {
                if (ct.IsCancellationRequested) { CrashLogger.Info("probe:cancelled"); CloseProgressDialog(); return; }
                try
                {
                    // ProbeVideo returns (gotThumb, gotDims) directly because both fields
                    // are assigned via Dispatcher.BeginInvoke and would race with a check
                    // on item.Thumbnail / item.PixelWidth here.
                    var (gotThumb, gotDims) = ProbeVideo(item, ct);
                    if (gotThumb) thumbsAssigned++;
                    if (gotDims) dimsAssigned++;
                }
                catch (Exception ex)
                {
                    failures++;
                    CrashLogger.Log(ex, $"probe:{item.FullPath}");
                }
                processed++;
                PostStatus(processed, force: false);
            }

            CrashLogger.Info($"probe:done thumbs={thumbsAssigned} dims={dimsAssigned} failures={failures}");

            if (ct.IsCancellationRequested) { CloseProgressDialog(); return; }

            // Probe is fully done — close the popup (with min-visible enforcement).
            // The flush timer is stopped inside CloseProgressDialog so the last batch
            // of pending updates is applied before the user sees "done".
            CloseProgressDialog();

            // Capture for the dispatcher closure.
            int finalThumbs = thumbsAssigned;
            int finalFails = failures;
            int finalTotal = items.Count;

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_settings.SortKey == SortKey.Aspect || _settings.SortKey == SortKey.Duration)
                        ApplySort();

                    if (reportCompletion)
                    {
                        StatusText.Text = finalFails > 0
                            ? $"Thumbnails regenerated: {finalThumbs:N0} of {finalTotal:N0} ({finalFails:N0} failed)"
                            : $"Thumbnails regenerated: {finalThumbs:N0} of {finalTotal:N0}";
                    }
                    else
                    {
                        // Restore the steady-state status text so users don't keep
                        // seeing "Loading thumbnails…" forever.
                        StatusText.Text = $"{finalTotal:N0} media file(s) found";
                    }
                }));
            }
            catch (Exception ex) { CrashLogger.Log(ex, "probe:final-sort"); }

            // Local helper — closes the progress dialog from a non-UI thread,
            // honoring min-visible time so the dialog doesn't just flash.
            // ALSO stops the probe-flush timer and drains any remaining updates so
            // the user doesn't see a tail of un-applied thumbnails after "done".
            async void CloseProgressDialog()
            {
                try { probeCancelReg.Dispose(); } catch { }
                try
                {
                    if (probeDialogShownAt != default)
                    {
                        var visibleFor = (DateTime.UtcNow - probeDialogShownAt).TotalMilliseconds;
                        var remaining = minVisibleMs - (int)visibleFor;
                        if (remaining > 0)
                        {
                            try { await Task.Delay(remaining); } catch { }
                        }
                    }
                }
                catch { }
                // Stop the flush timer + drain pending updates on the UI thread.
                try
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { StopProbeFlushTimer(); } catch { }
                        try { progressDialog?.Close(); } catch { }
                    }));
                }
                catch { }
            }
        }, ct);
    }

    /// <summary>Returns true if a thumbnail was successfully decoded and assigned.</summary>
    private bool ProbeImage(MediaItem item, CancellationToken ct)
    {
        // Dimensions (cheap, metadata-only). We still read them here so they're
        // available to fold into the same batched UI update as the thumbnail below.
        int dimW = 0, dimH = 0;
        try
        {
            var (w, h) = ThumbnailLoader.TryReadImageDimensions(item.FullPath);
            if (w > 0 && h > 0) { dimW = w; dimH = h; }
        }
        catch (Exception ex) { CrashLogger.Log(ex, $"dims:{item.FullPath}"); }

        if (ct.IsCancellationRequested) return false;

        // (#2) Two-tier cache lookup: memory → disk → decode-and-store. The cache key encodes
        // path + mtime + size + decode width, so edits and re-saves invalidate cleanly.
        const int ThumbDecodeWidth = 128;
        BitmapSource? thumb = ThumbnailCache.TryGetMemory(item.FullPath, ThumbDecodeWidth)
                              ?? ThumbnailCache.TryGetDisk(item.FullPath, ThumbDecodeWidth);

        if (thumb == null)
        {
            // (memory fix) Stream from disk and decode straight to the requested width.
            // The previous path read the entire file into a byte[] then decoded it,
            // which on a 1938-image network share caused tens of GB of RAM use because
            // every 5–50 MB raw byte buffer stayed pinned until decode finished.
            try
            {
                thumb = ThumbnailLoader.LoadImageThumbnailFromFile(item.FullPath, ThumbDecodeWidth);
                if (thumb == null)
                {
                    CrashLogger.Info($"probe:decode-null {item.FileName}");
                    return false;
                }
                // Persist for next time.
                try { ThumbnailCache.Put(item.FullPath, ThumbDecodeWidth, thumb); }
                catch (Exception ex) { CrashLogger.Log(ex, $"thumb-cache-put:{item.FullPath}"); }
            }
            catch (Exception ex)
            {
                CrashLogger.Log(ex, $"probe-image-bg:{item.FullPath}");
                return false;
            }
        }

        if (ct.IsCancellationRequested) return false;

        try
        {
            // Single batched update: dimensions + thumbnail in one ProbeUpdate so
            // the UI thread applies them together (one PropertyChanged burst per item).
            EnqueueProbeUpdate(item, dimW, dimH, dur: 0, thumb: thumb);
            return true;
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"enqueue-assign:{item.FullPath}");
            return false;
        }
    }

    /// <summary>Returns (gotThumbnail, gotDimensions) — the caller can't observe
    /// item.Thumbnail / item.PixelWidth synchronously because those fields are
    /// assigned later by the batched UI flush timer.</summary>
    private (bool gotThumb, bool gotDims) ProbeVideo(MediaItem item, CancellationToken ct)
    {
        bool gotDims = false;
        int vw = 0, vh = 0;
        double vDur = 0;
        try
        {
            var (w, h, dur) = VideoProbe.TryReadVideoInfo(item.FullPath);
            if (ct.IsCancellationRequested) return (false, false);
            if (w > 0 && h > 0) { vw = w; vh = h; gotDims = true; }
            if (dur > 0) vDur = dur;
        }
        catch (Exception ex) { CrashLogger.Log(ex, $"video-probe:{item.FullPath}"); }

        if (ct.IsCancellationRequested) return (false, gotDims);

        // Video thumbnail via Windows Shell (same source as Explorer's preview).
        // Cached identically to image thumbnails so a second scan is instant.
        // Use 256px so the shell extracts a real video frame instead of scaling
        // up the generic 128px file icon.
        try
        {
            const int ThumbDecodeWidth = 256;
            BitmapSource? thumb = ThumbnailCache.TryGetMemory(item.FullPath, ThumbDecodeWidth)
                                  ?? ThumbnailCache.TryGetDisk(item.FullPath, ThumbDecodeWidth);
            if (thumb == null)
            {
                thumb = ThumbnailLoader.LoadShellThumbnail(item.FullPath, ThumbDecodeWidth);
                if (thumb != null)
                {
                    try { ThumbnailCache.Put(item.FullPath, ThumbDecodeWidth, thumb); }
                    catch (Exception ex) { CrashLogger.Log(ex, $"video-thumb-cache-put:{item.FullPath}"); }
                }
            }

            if (thumb != null && !ct.IsCancellationRequested)
            {
                // Batched: dims + duration + thumbnail go together.
                EnqueueProbeUpdate(item, vw, vh, vDur, thumb);
                return (true, gotDims);
            }
            // Even without a thumbnail, push the dims+duration we found so the
            // grid columns populate.
            if (vw > 0 || vDur > 0)
            {
                EnqueueProbeUpdate(item, vw, vh, vDur, thumb: null);
            }
            return (false, gotDims);
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, $"video-thumb:{item.FullPath}");
            return (false, gotDims);
        }
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
                    // (#15) Friendlier message for HEIC / RAW formats that depend on optional Microsoft codecs.
                    var ext = item.Extension.ToLowerInvariant();
                    string msg;
                    if (Models.MediaFormats.IsHeif(ext))
                        msg = $"Cannot decode {ext}.\nInstall the \u201cHEIF Image Extensions\u201d package from the Microsoft Store to preview HEIC/HEIF files.";
                    else if (Models.MediaFormats.IsRaw(ext))
                        msg = $"Cannot decode {ext}.\nNo embedded preview was found, and the Microsoft \u201cRaw Image Extension\u201d (or your camera vendor\u2019s codec) does not appear to be installed.";
                    else
                        msg = $"Cannot decode {ext}";
                    PreviewEmpty.Text = msg;
                    PreviewEmpty.Visibility = Visibility.Visible;
                    StatusText.Text = $"Cannot decode {item.FileName}";
                    return;
                }

                PreviewImage.Source = bmp;
                PreviewImage.Visibility = Visibility.Visible;
                // Default zoom 0.9 so the image breathes a little within the viewport.
                PreviewImageScale.ScaleX = DefaultPreviewZoom;
                PreviewImageScale.ScaleY = DefaultPreviewZoom;
                PreviewScroll.ScrollToHorizontalOffset(0);
                PreviewScroll.ScrollToVerticalOffset(0);
                PreviewEmpty.Visibility = Visibility.Collapsed;

                PopulateExif(item);
            }
            else if (item.Kind == MediaKind.Video)
            {
                PreviewImage.Source = null;
                PreviewImage.Visibility = Visibility.Collapsed;
                _ = StartVideoPreviewAsync(item); // (#4) lazy-init LibVLC if needed
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

            var st = _mediaPlayer.State;

            // Currently playing → pause (use SetPause for unambiguous behavior).
            if (st == VLCState.Playing)
            {
                _mediaPlayer.SetPause(true);
                return;
            }

            // Paused → resume in place. SetPause(false) is the deterministic API
            // for unpausing in LibVLCSharp; Play() is unreliable here.
            if (st == VLCState.Paused)
            {
                _mediaPlayer.SetPause(false);
                return;
            }

            // Ended / Stopped / Error / fresh player → reload current item from start.
            if (st == VLCState.Ended || st == VLCState.Stopped || st == VLCState.Error || st == VLCState.NothingSpecial)
            {
                var sel = GetSelectedItems().FirstOrDefault();
                if (sel != null && sel.Kind == MediaKind.Video && _libVlc != null)
                {
                    using var media = new Media(_libVlc, new Uri(sel.FullPath));
                    _mediaPlayer.Play(media);
                    ApplyAudioPreviewState();
                    _videoTimer?.Start();
                    return;
                }
            }

            // Buffering / Opening / other transient → nudge with Play().
            _mediaPlayer.Play();
        }
        catch { }
    }

    // ===================== (#20) AUDIO PREVIEW =====================
    // Per-session mute toggle and volume slider for video preview. State persists
    // across sessions in AppSettings.VideoMuted / VideoVolume, applied on each
    // new media load (LibVLC resets per-media volume on Play()).

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || _settings == null) return;
        var v = (int)Math.Round(e.NewValue);
        if (v < 0) v = 0; if (v > 100) v = 100;
        _settings.VideoVolume = v;
        // Adjusting volume implicitly unmutes — matches every other media app.
        if (v > 0 && _settings.VideoMuted)
        {
            _settings.VideoMuted = false;
            MuteToggle.IsChecked = false;
            MuteToggle.Content = "\ud83d\udd0a";
        }
        ApplyAudioPreviewState();
        SaveSettings();
    }

    private void MuteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        _settings.VideoMuted = MuteToggle.IsChecked == true;
        MuteToggle.Content = _settings.VideoMuted ? "\ud83d\udd07" : "\ud83d\udd0a";
        ApplyAudioPreviewState();
        SaveSettings();
    }

    /// <summary>
    /// (#20) Push the current mute / volume state into LibVLC. Safe to call before
    /// the media player is initialized — it just no-ops.
    /// </summary>
    private void ApplyAudioPreviewState()
    {
        var mp = _mediaPlayer;
        if (mp == null || _settings == null) return;
        try
        {
            mp.Volume = Math.Max(0, Math.Min(100, _settings.VideoVolume));
            mp.Mute   = _settings.VideoMuted;
        }
        catch { /* LibVLC native call may fail if media not ready; will retry on next Play */ }
    }

    private void ToggleMuteFromKeyboard()
    {
        if (_settings == null) return;
        _settings.VideoMuted = !_settings.VideoMuted;
        MuteToggle.IsChecked = _settings.VideoMuted;
        MuteToggle.Content = _settings.VideoMuted ? "\ud83d\udd07" : "\ud83d\udd0a";
        ApplyAudioPreviewState();
        StatusText.Text = _settings.VideoMuted ? "Audio muted" : $"Audio on ({_settings.VideoVolume}%)";
        SaveSettings();
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

    /// <summary>Default zoom factor applied to a freshly loaded preview image
    /// (also the value that the 0 / NumPad0 reset hotkey returns to). 0.9 leaves
    /// breathing room around the image instead of pushing it against the edges.</summary>
    private const double DefaultPreviewZoom = 0.9;

    private void ResetImageZoom()
    {
        PreviewImageScale.ScaleX = DefaultPreviewZoom;
        PreviewImageScale.ScaleY = DefaultPreviewZoom;
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
            RefreshVolumeWatchList(); // (#16)
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
                RefreshVolumeWatchList(); // (#16) folder may have changed
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
                RefreshVolumeWatchList(); // (#16)
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

    // ===================== (#10) DRAG-HANDLE REORDER =====================
    // Click-and-hold reorder per the user's standing rule (NO native drag-and-drop).
    // The handle captures the mouse on press; while held, MouseMove hit-tests the
    // destinations panel and swaps the held row with whichever row's vertical midpoint
    // the cursor crosses. Release ends the gesture and persists the new order.

    private DestinationButton? _dragHandleItem;
    private System.Windows.UIElement? _dragHandleSource;
    private bool _dragHandleReordering;

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DestinationButton dest) return;
        _dragHandleItem = dest;
        _dragHandleSource = fe;
        _dragHandleReordering = true;
        try { fe.CaptureMouse(); } catch { }
        fe.MouseMove += DragHandle_MouseMove;
        fe.PreviewMouseLeftButtonUp += DragHandle_PreviewMouseLeftButtonUp;
        fe.LostMouseCapture += DragHandle_LostMouseCapture;
        e.Handled = true;
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragHandleReordering || _dragHandleItem == null) return;
        if (DestinationsPanel == null) return;

        // Convert cursor to DestinationsPanel coordinate space.
        var pos = e.GetPosition(DestinationsPanel);

        // Walk every materialized row container and find the one whose vertical midpoint
        // the cursor has crossed. This works whether the panel virtualizes or not because
        // we only care about currently-visible containers.
        for (int i = 0; i < Destinations.Count; i++)
        {
            if (DestinationsPanel.ItemContainerGenerator.ContainerFromIndex(i)
                is not FrameworkElement container) continue;
            try
            {
                var topLeft = container.TranslatePoint(new System.Windows.Point(0, 0), DestinationsPanel);
                var midY = topLeft.Y + (container.ActualHeight / 2.0);
                if (pos.Y < midY)
                {
                    int currentIdx = Destinations.IndexOf(_dragHandleItem);
                    if (currentIdx >= 0 && currentIdx != i)
                    {
                        Destinations.Move(currentIdx, i);
                    }
                    return;
                }
            }
            catch { /* container may not be laid out yet */ }
        }

        // Past the last midpoint — drop at the bottom.
        int lastIdx = Destinations.Count - 1;
        int curIdx = Destinations.IndexOf(_dragHandleItem);
        if (curIdx >= 0 && curIdx != lastIdx)
            Destinations.Move(curIdx, lastIdx);
    }

    private void DragHandle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => EndDragHandleReorder();

    private void DragHandle_LostMouseCapture(object sender, MouseEventArgs e)
        => EndDragHandleReorder();

    private void EndDragHandleReorder()
    {
        if (!_dragHandleReordering) return;
        _dragHandleReordering = false;
        if (_dragHandleSource is FrameworkElement fe)
        {
            fe.MouseMove -= DragHandle_MouseMove;
            fe.PreviewMouseLeftButtonUp -= DragHandle_PreviewMouseLeftButtonUp;
            fe.LostMouseCapture -= DragHandle_LostMouseCapture;
            try { fe.ReleaseMouseCapture(); } catch { }
        }
        _dragHandleSource = null;
        _dragHandleItem = null;
        SaveSettings();
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
            // (#21) Ctrl+Click on a destination opens its folder in Explorer instead
            // of dispatching a Move/Copy. Saves a trip to the tiny 📂 button.
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (Directory.Exists(dest.FolderPath))
                {
                    try
                    {
                        System.Diagnostics.Process.Start("explorer.exe", $"\"{dest.FolderPath}\"");
                        StatusText.Text = $"Opened '{dest.Name}' in Explorer";
                    }
                    catch (Exception ex)
                    {
                        StatusText.Text = $"Could not open folder: {ex.Message}";
                    }
                }
                else
                {
                    StatusText.Text = $"Folder does not exist: {dest.FolderPath}";
                }
                return;
            }

            var items = GetSelectedItems();
            if (items.Count == 0) { StatusText.Text = "No item selected."; return; }
            _lastDestination = dest; // (#8) remember for `.` repeat
            DispatchAction(items, dest, fe);
        }
    }

    // Single chokepoint for sending items to a destination, honoring the
    // toolbar Action dropdown: Move (default), Copy (keep originals), or
    // Delete (recycle originals without copying). Used by button click,
    // hotkeys, and any future trigger so the action setting is never bypassed.
    private void DispatchAction(List<MediaItem> items, DestinationButton dest, FrameworkElement? destinationElement)
    {
        // (#17) Per-destination override wins over the toolbar selector when set.
        var effective = dest.ActionOverride ?? _settings.Action;
        switch (effective)
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
    /// Resolve a subfolder template to a relative path. Supports tokens (#14):
    ///   {date[:fmt]}   — file last-write date (default yyyy-MM)
    ///   {exif[:fmt]}   — EXIF DateTaken, falls back to {date} (default yyyy-MM)
    ///   {kind}         — "Images" / "Videos" / "Other"
    ///   {ext}          — file extension without the dot, lowercased
    ///   {camera}       — EXIF camera manufacturer + model (sanitized)
    ///   {iso}          — EXIF ISO speed
    /// Tokens that can't be resolved fall back to a sensible default rather than
    /// leaving the literal {token} in the path. Invalid filename chars are stripped
    /// from the final result.
    /// </summary>
    private static string ResolveSubfolder(string template, string sourcePath)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        var fi = new FileInfo(sourcePath);
        var fileDate = fi.Exists ? fi.LastWriteTime : DateTime.Now;
        var ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        string kind = "Other";
        if (Models.MediaFormats.ImageExtensions.Contains("." + ext)) kind = "Images";
        else if (Models.MediaFormats.VideoExtensions.Contains("." + ext)) kind = "Videos";

        // EXIF data is read lazily — only when an exif/camera/iso token is referenced.
        DateTime? exifDate = null; bool exifDateChecked = false;
        string? camera = null; bool cameraChecked = false;
        string? iso = null; bool isoChecked = false;

        DateTime ExifDate()
        {
            if (!exifDateChecked) { exifDate = MediaSort.Services.ExifReader.TryGetDateTaken(sourcePath); exifDateChecked = true; }
            return exifDate ?? fileDate;
        }
        string Camera()
        {
            if (!cameraChecked) { camera = MediaSort.Services.ExifReader.TryGetTag(sourcePath, "camera"); cameraChecked = true; }
            return string.IsNullOrWhiteSpace(camera) ? "UnknownCamera" : camera!;
        }
        string Iso()
        {
            if (!isoChecked) { iso = MediaSort.Services.ExifReader.TryGetTag(sourcePath, "iso"); isoChecked = true; }
            return string.IsNullOrWhiteSpace(iso) ? "ISO0" : "ISO" + iso;
        }

        var resolved = System.Text.RegularExpressions.Regex.Replace(template,
            @"\{(\w+)(?::([^}]+))?\}", m =>
            {
                var token = m.Groups[1].Value.ToLowerInvariant();
                var fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
                return token switch
                {
                    "date" => fileDate.ToString(string.IsNullOrEmpty(fmt) ? "yyyy-MM" : fmt),
                    "exif" => ExifDate().ToString(string.IsNullOrEmpty(fmt) ? "yyyy-MM" : fmt),
                    "kind" => kind,
                    "ext" => ext,
                    "camera" => Camera(),
                    "iso" => Iso(),
                    _ => m.Value
                };
            });

        // Strip invalid filename chars from each segment so users can write "Photos/{exif:yyyy/MM}"
        // — keep '/' and '\' as separators.
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(resolved.Length);
        foreach (var c in resolved)
        {
            if (c == '/' || c == '\\') sb.Append(c);
            else if (Array.IndexOf(invalid, c) < 0) sb.Append(c);
        }
        return sb.ToString();
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
        var sub = ResolveSubfolder(dest.SubfolderTemplate, sourcePath);
        if (!string.IsNullOrEmpty(sub))
            targetFolder = Path.Combine(targetFolder, sub);

        var probable = Path.Combine(targetFolder, Path.GetFileName(sourcePath));
        if (File.Exists(probable) && policy == ConflictPolicy.Prompt && !applyAll)
        {
            var dlg = new ConflictDialog(sourcePath, probable) { Owner = this };
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
        // progress bar + Cancel button when the source is >= threshold, so the UI
        // stays responsive on big copies. (#3) On cross-volume copies use the lower
        // CrossVolumeThreshold (default 5 MB) since the I/O is inherently slow.
        try
        {
            var fi = new System.IO.FileInfo(sourcePath);
            long threshold = IsCrossVolume(sourcePath, targetFolder)
                ? FileMoverProgress.CrossVolumeThreshold
                : FileMoverProgress.LargeFileThreshold;
            if (fi.Exists && fi.Length >= threshold)
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
        var sub = ResolveSubfolder(dest.SubfolderTemplate, sourcePath);
        if (!string.IsNullOrEmpty(sub))
            targetFolder = Path.Combine(targetFolder, sub);

        // Decide policy: if Prompt and a conflict will occur, ask once (then maybe apply to all)
        var fileNamePreview = string.IsNullOrEmpty(dest.RenameTemplate)
            ? Path.GetFileName(sourcePath)
            : Path.GetFileName(Path.Combine(targetFolder, "x")); // template applied later

        var probable = Path.Combine(targetFolder, Path.GetFileName(sourcePath));
        if (File.Exists(probable) && policy == ConflictPolicy.Prompt && !applyAll)
        {
            var dlg = new ConflictDialog(sourcePath, probable) { Owner = this };
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
        // (#3) Cross-volume moves are physically copies, so even "medium" files (≥5 MB) deserve
        // the progress dialog — otherwise the UI freezes on slow USB / network targets.
        try
        {
            var fi = new System.IO.FileInfo(sourcePath);
            long threshold = IsCrossVolume(sourcePath, targetFolder)
                ? FileMoverProgress.CrossVolumeThreshold
                : FileMoverProgress.LargeFileThreshold;
            if (fi.Exists && fi.Length >= threshold)
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
    /// <summary>
    /// (#3) True when the source and destination live on different volumes. Used to drop
    /// the threshold for the chunked progress-dialog path — cross-volume "moves" are
    /// physically copies, so even mid-sized files can stall the UI on slow drives.
    /// </summary>
    private static bool IsCrossVolume(string sourcePath, string destFolder)
    {
        try
        {
            var srcRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(sourcePath));
            var dstRoot = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(destFolder));
            if (string.IsNullOrEmpty(srcRoot) || string.IsNullOrEmpty(dstRoot)) return false;
            return !string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

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

        // (#18) Restore selection to the items we just brought back so the user can
        // immediately re-act on them (e.g. to send them somewhere else). Defer to
        // Loaded so item containers exist before we touch SelectedItems.
        var restoredItems = restoredPairs.Select(p => p.item).ToList();
        if (restoredItems.Count > 0)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var sel = ActiveSelector;
                if (sel == null) return;
                try
                {
                    sel.SelectedItems.Clear();
                    foreach (var it in restoredItems)
                    {
                        if (MediaItems.Contains(it)) sel.SelectedItems.Add(it);
                    }
                    if (sel.SelectedItems.Count > 0)
                    {
                        var first = sel.SelectedItems[0];
                        sel.ScrollIntoView(first);
                        UpdateStats();
                    }
                }
                catch { /* layout race — non-critical */ }
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

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

    /// <summary>
    /// Find near-duplicate images using a perceptual hash (pHash) and surface the
    /// results in the modal <see cref="DuplicatesDialog"/>. The dialog lets the
    /// user pick which copy to keep in each group; on Apply we move the
    /// non-kept items to the chosen destination using the regular MoveItemsTo
    /// pipeline so animations, conflict prompts, undo and the +N flash all work
    /// the same as a normal hot-key move.
    ///
    /// Hashing runs on a worker thread behind a ProgressDialog so the UI stays
    /// responsive on big folders. Already-hashed items are skipped on subsequent
    /// invocations.
    /// </summary>
    private async void FindDuplicates_Click(object sender, RoutedEventArgs e)
    {
        var images = _allItems.Where(m => m.Kind == MediaKind.Image).ToList();
        if (images.Count < 2) { StatusText.Text = "Need at least 2 images to compare."; return; }

        // Reset DUP badges from any previous run before we recompute.
        foreach (var m in images) m.IsDuplicate = false;

        var toHash = images.Where(m => string.IsNullOrEmpty(m.PerceptualHash)).ToList();

        // Show the progress dialog immediately so the user sees feedback even on
        // small batches that finish before the first ReportProgress tick.
        var progress = new ProgressDialog("Finding duplicates…",
                                          $"Hashing {toHash.Count} image(s)")
        {
            Owner = this
        };
        var ct = progress.Token;

        // Drive the hashing on a worker thread so we can pump progress updates
        // into the dialog. We use Task.Run + the thread pool here (not
        // Parallel.ForEach) because perceptual hashing decodes JPEGs and we
        // already cap CPU elsewhere via BelowNormal threads.
        var hashTask = Task.Run(() =>
        {
            int done = 0;
            int total = toHash.Count;
            foreach (var m in toHash)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var h = PerceptualHasher.Hash(m.FullPath);
                    Dispatcher.Invoke(() => m.PerceptualHash = h);
                }
                catch { /* unreadable file: leave hash empty so it just won't match anything */ }
                done++;
                int snapshotDone = done;
                progress.ReportProgress(snapshotDone, total);
                progress.SetDetail($"Hashed {snapshotDone} / {total} · {Path.GetFileName(m.FullPath)}");
            }
        }, ct);

        progress.ShowDialog();    // blocks until dialog closes (Cancel) or we close it below
        try { await hashTask; } catch { }
        // If the user cancelled, the dialog's Closed handler already cancelled the token.
        if (progress.Token.IsCancellationRequested && hashTask.Status != TaskStatus.RanToCompletion)
        {
            // Dialog already closed when Cancel was clicked. Nothing else to clean up.
            StatusText.Text = "Find duplicates cancelled.";
            return;
        }

        // The progress dialog auto-closed only on Cancel; if hashing completed naturally
        // we need to close it ourselves. WPF's ShowDialog already returned by now if the
        // user hit Cancel. If hashing finished first the dialog is still open, so close it.
        if (progress.IsVisible) progress.Close();

        // Build duplicate groups via union-find: every pair within Hamming distance 6
        // becomes a single component, so a chain a~b~c collapses into one group even
        // when a and c are not directly within threshold.
        var groups = BuildDuplicateGroups(images, threshold: 6);

        if (groups.Count == 0)
        {
            StatusText.Text = "No near-duplicates found.";
            MessageBox.Show(this,
                $"No duplicates found.\n\nScanned {images.Count} image(s).",
                "Find Duplicates",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Light up DUP badges in the source list so the visual cue is consistent
        // with what the dialog shows.
        foreach (var g in groups)
            foreach (var m in g.Members)
                m.IsDuplicate = true;

        var dlg = new DuplicatesDialog(groups, Destinations) { Owner = this };
        var result = dlg.ShowDialog();

        if (result != true || !dlg.ApplyRequested || dlg.ChosenDestination == null || dlg.ItemsToMove.Count == 0)
        {
            StatusText.Text = $"Found {groups.Count} duplicate group(s) · DUP badges shown in Thumbnails view.";
            return;
        }

        // Apply: hand the non-kept items off to the standard move pipeline. It already
        // handles destination kind filters, conflict prompts, animations, undo, and the
        // +N flash badge.
        MoveItemsTo(dlg.ItemsToMove, dlg.ChosenDestination, FindDestinationElement(dlg.ChosenDestination));
        StatusText.Text = $"Moved {dlg.ItemsToMove.Count} duplicate(s) to {dlg.ChosenDestination.Name}.";
    }

    /// <summary>
    /// Cluster items whose perceptual hashes are within <paramref name="threshold"/>
    /// Hamming distance into groups using a simple union-find. Singletons are
    /// dropped — we only return groups of 2+. Items missing a hash never match
    /// anything.
    /// </summary>
    private static List<DuplicateGroup> BuildDuplicateGroups(List<MediaItem> images, int threshold)
    {
        int n = images.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b)
        {
            int ra = Find(a), rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        for (int i = 0; i < n; i++)
        {
            if (string.IsNullOrEmpty(images[i].PerceptualHash)) continue;
            for (int j = i + 1; j < n; j++)
            {
                if (string.IsNullOrEmpty(images[j].PerceptualHash)) continue;
                if (PerceptualHasher.Distance(images[i].PerceptualHash, images[j].PerceptualHash) <= threshold)
                    Union(i, j);
            }
        }

        var buckets = new Dictionary<int, List<MediaItem>>();
        for (int i = 0; i < n; i++)
        {
            if (string.IsNullOrEmpty(images[i].PerceptualHash)) continue;
            int root = Find(i);
            if (!buckets.TryGetValue(root, out var list)) buckets[root] = list = new List<MediaItem>();
            list.Add(images[i]);
        }

        return buckets.Values.Where(g => g.Count >= 2).Select(g => new DuplicateGroup(g)).ToList();
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

    // ----------------- RENAME (#6) -----------------

    /// <summary>
    /// F2 / context-menu rename. Single-selection only. Validates collisions, then
    /// renames on disk and updates the MediaItem in place so bindings refresh.
    /// </summary>
    private void RenameSelected()
    {
        var sel = GetSelectedItems();
        if (sel.Count == 0)
        {
            StatusText.Text = "Select a file to rename (F2).";
            return;
        }
        if (sel.Count > 1)
        {
            BatchRenameSelected(sel);
            return;
        }

        var item = sel[0];
        if (!File.Exists(item.FullPath))
        {
            StatusText.Text = $"File no longer exists: {item.FileName}";
            return;
        }

        var dlg = new RenameDialog(item.FullPath) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.NewFileName)) return;

        var dir = Path.GetDirectoryName(item.FullPath) ?? "";
        var newPath = Path.Combine(dir, dlg.NewFileName);

        var oldPath = item.FullPath;
        var wasFavorite = item.IsFavorite;
        try
        {
            File.Move(oldPath, newPath);
            item.UpdateAfterRename(newPath);

            PreviewTitle.Text = $"Preview — {item.FileName}";
            StatusText.Text = $"Renamed to {item.FileName}";

            // Update favorites store so the star survives the path change.
            if (wasFavorite)
            {
                _settings.Favorites.RemoveAll(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
                if (!_settings.Favorites.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                    _settings.Favorites.Add(newPath);
                SaveSettings();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Rename failed: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Rename failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e) => RenameSelected();

    /// <summary>
    /// Multi-file rename via the BatchRenameDialog. Applies the user's pattern to every
    /// selected item, validates collisions, then renames each file in place.
    /// </summary>
    private void BatchRenameSelected(List<MediaItem> items)
    {
        var dlg = new BatchRenameDialog(items) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Plan.Count == 0) return;

        int ok = 0, fail = 0;
        foreach (var (oldPath, newPath) in dlg.Plan)
        {
            var item = items.FirstOrDefault(m => string.Equals(m.FullPath, oldPath, StringComparison.OrdinalIgnoreCase));
            if (item == null) continue;
            try
            {
                bool wasFav = item.IsFavorite;
                File.Move(oldPath, newPath);
                item.UpdateAfterRename(newPath);
                if (wasFav)
                {
                    _settings.Favorites.RemoveAll(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
                    if (!_settings.Favorites.Contains(newPath, StringComparer.OrdinalIgnoreCase))
                        _settings.Favorites.Add(newPath);
                }
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                CrashLogger.Info($"batch-rename-fail {oldPath} → {newPath}: {ex.Message}");
            }
        }
        if (fail > 0) SaveSettings(); else if (ok > 0) SaveSettings();
        StatusText.Text = fail == 0
            ? $"Renamed {ok} file{(ok == 1 ? "" : "s")}."
            : $"Renamed {ok}, {fail} failed (see crash.log).";
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
    /// (#11) Toggle the favorite/star flag on each currently-selected item. If at least one
    /// is unfavorited, all become favorited; otherwise all become unfavorited ("toggle to a
    /// consistent state" feels right when batches are mixed).
    /// </summary>
    private void ToggleFavoriteOnSelection()
    {
        var sel = GetSelectedItems();
        if (sel.Count == 0) { StatusText.Text = "Select an item first to star it."; return; }

        bool anyUnfav = sel.Any(i => !i.IsFavorite);
        bool newState = anyUnfav; // promote all to true if any are off

        var favSet = new HashSet<string>(_settings.Favorites, StringComparer.OrdinalIgnoreCase);
        foreach (var it in sel)
        {
            it.IsFavorite = newState;
            if (newState) favSet.Add(it.FullPath);
            else          favSet.Remove(it.FullPath);
        }

        // Cap at a sane size so settings.json doesn't grow forever if the user goes wild.
        const int MaxFavorites = 5000;
        var list = favSet.ToList();
        if (list.Count > MaxFavorites) list = list.GetRange(list.Count - MaxFavorites, MaxFavorites);
        _settings.Favorites = list;
        SaveSettings();

        StatusText.Text = newState
            ? $"Starred {sel.Count} file(s)"
            : $"Removed star from {sel.Count} file(s)";
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

        // (#11) F toggles favorite/star on the current selection. Skipped if a destination
        // already binds F as its own hotkey.
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.None)
        {
            bool ownedByDest = Destinations.Any(d => d.HotKey == Key.F && d.Modifiers == ModifierKeys.None);
            if (!ownedByDest)
            {
                ToggleFavoriteOnSelection();
                e.Handled = true;
                return;
            }
        }

        // (#20) M toggles audio mute for video preview. Skipped if a destination
        // owns M as its own hotkey.
        if (e.Key == Key.M && Keyboard.Modifiers == ModifierKeys.None)
        {
            bool ownedByDest = Destinations.Any(d => d.HotKey == Key.M && d.Modifiers == ModifierKeys.None);
            if (!ownedByDest)
            {
                ToggleMuteFromKeyboard();
                e.Handled = true;
                return;
            }
        }

        // (#8) `.` (or numpad `.`) repeats the last destination action. Skipped if
        // a destination already binds OemPeriod / Decimal as its own hotkey — their
        // own hotkey wins.
        if ((e.Key == Key.OemPeriod || e.Key == Key.Decimal) && Keyboard.Modifiers == ModifierKeys.None)
        {
            bool ownedByDest = Destinations.Any(d =>
                (d.HotKey == Key.OemPeriod || d.HotKey == Key.Decimal) && d.Modifiers == ModifierKeys.None);
            if (!ownedByDest)
            {
                if (_lastDestination == null)
                {
                    StatusText.Text = "No previous destination to repeat. Send to a destination first, then press `.` to repeat.";
                }
                else
                {
                    var items = GetSelectedItems();
                    if (items.Count > 0)
                    {
                        DispatchAction(items, _lastDestination, FindDestinationElement(_lastDestination));
                    }
                }
                e.Handled = true;
                return;
            }
        }

        // (#6) F2 = rename. Single selection → simple rename dialog. Multi-selection → batch rename dialog with pattern tokens.
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None)
        {
            RenameSelected();
            e.Handled = true;
            return;
        }

        // (#9) Esc clears any in-progress multi-destination queue.
        if (e.Key == Key.Escape && _destQueue.Count > 0)
        {
            ClearDestinationQueue("Multi-destination queue cancelled.");
            e.Handled = true;
            return;
        }

        // Destination hotkeys
        foreach (var dest in Destinations)
        {
            if (dest.HotKey == Key.None) continue;

            // (#9) Shift+<dest hotkey> queues for multi-destination split. We only
            // intercept when the destination's own modifier set does NOT include Shift,
            // so a destination explicitly bound to Shift+K still fires normally.
            bool shiftHeld = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool destOwnsShift = (dest.Modifiers & ModifierKeys.Shift) != 0;
            if (shiftHeld && !destOwnsShift && dest.HotKey == e.Key &&
                (Keyboard.Modifiers & ~ModifierKeys.Shift) == dest.Modifiers)
            {
                EnqueueDestination(dest);
                e.Handled = true;
                return;
            }

            if (dest.HotKey == e.Key && Keyboard.Modifiers == dest.Modifiers)
            {
                var items = GetSelectedItems();
                if (items.Count == 0) return;
                _lastDestination = dest;
                DispatchAction(items, dest, FindDestinationElement(dest));
                e.Handled = true;
                return;
            }
        }
    }

    /// <summary>
    /// (#9) Fires when the user releases a key. We only act on Shift release — if there's
    /// a queued list of destinations, dispatch them now: every entry except the last as
    /// Copy, the last as the toolbar action (Move/Copy/Delete).
    /// </summary>
    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.LeftShift && e.Key != Key.RightShift) return;
        // Still some Shift down? (one of the two Shift keys held, the other released)
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        if (_destQueue.Count == 0) return;
        DispatchDestinationQueue();
    }

    private void EnqueueDestination(DestinationButton dest)
    {
        if (_destQueueItems == null)
            _destQueueItems = GetSelectedItems();

        if (_destQueueItems == null || _destQueueItems.Count == 0)
        {
            StatusText.Text = "Select item(s) before queuing destinations.";
            return;
        }
        if (_destQueue.Contains(dest)) return; // ignore duplicate hotkey taps
        _destQueue.Add(dest);

        // Status bar chip strip: "Multi-dest: K → B → R (release Shift to send, Esc cancels)"
        var chips = string.Join(" → ", _destQueue.Select(d => d.Name));
        StatusText.Text = $"Multi-dest [{_destQueueItems.Count} item(s)]: {chips}  (release Shift to send, Esc to cancel)";
        FlashDestinationBadge(dest, _destQueueItems.Count);
    }

    private void ClearDestinationQueue(string status)
    {
        _destQueue.Clear();
        _destQueueItems = null;
        StatusText.Text = status;
    }

    private void DispatchDestinationQueue()
    {
        if (_destQueue.Count == 0 || _destQueueItems == null) { _destQueue.Clear(); _destQueueItems = null; return; }

        var items = _destQueueItems;
        var queue = _destQueue.ToList();
        // Reset state up front so re-entry is safe.
        _destQueue.Clear();
        _destQueueItems = null;

        // Remember the user's chosen Action; we need to force Copy for all but the last
        // entry without permanently changing the setting. We also temporarily clear each
        // dest's per-destination ActionOverride (#17) for non-last entries so the
        // "copy all but the last" semantics is preserved — otherwise an override would
        // win over the forced Copy and could leave the original orphaned partway through.
        var savedAction = _settings.Action;
        var savedOverrides = queue.Select(d => d.ActionOverride).ToList();
        try
        {
            for (int i = 0; i < queue.Count; i++)
            {
                bool isLast = i == queue.Count - 1;
                _settings.Action = isLast ? savedAction : FileAction.Copy;
                var dest = queue[i];
                if (!isLast) dest.ActionOverride = null; // force Copy via toolbar fallback
                _lastDestination = dest;
                DispatchAction(items, dest, FindDestinationElement(dest));
            }
        }
        finally
        {
            _settings.Action = savedAction;
            for (int i = 0; i < queue.Count; i++)
                queue[i].ActionOverride = savedOverrides[i];
        }
        StatusText.Text = $"Sent {items.Count} item(s) to {queue.Count} destination(s): {string.Join(", ", queue.Select(d => d.Name))}";
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

    /// <summary>
    /// Persist the source / destinations panel widths whenever the user finishes
    /// dragging either GridSplitter. Only fires after _initializing flips to false,
    /// so the XAML defaults aren't immediately overwritten on startup.
    /// </summary>
    private void PanelSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_initializing || _settings == null) return;
        try
        {
            if (LeftPanelColumn != null && LeftPanelColumn.ActualWidth > 0)
                _settings.LeftPanelWidth = LeftPanelColumn.ActualWidth;
            if (RightPanelColumn != null && RightPanelColumn.ActualWidth > 0)
                _settings.RightPanelWidth = RightPanelColumn.ActualWidth;
            SaveSettings();
        }
        catch (Exception ex) { CrashLogger.Log(ex, "splitter-save"); }
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
