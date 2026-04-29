using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace MediaSort.Models;

public enum MediaKind
{
    Image,
    Video,
    Other
}

public class MediaItem : INotifyPropertyChanged
{
    private System.Windows.Media.Imaging.BitmapSource? _thumbnail;
    private int _pixelWidth;
    private int _pixelHeight;
    private double _durationSeconds;
    private string _perceptualHash = "";
    private bool _isDuplicate;
    private bool _isSelected;
    private bool _isFavorite;
    private string _fullPath = "";
    private string _fileName = "";
    private string _extension = "";
    private long _sizeBytes;
    private DateTime _modifiedDate;

    public string FullPath
    {
        get => _fullPath;
        private set { if (_fullPath == value) return; _fullPath = value; OnPropertyChanged(); }
    }
    public string FileName
    {
        get => _fileName;
        private set { if (_fileName == value) return; _fileName = value; OnPropertyChanged(); }
    }
    public string Extension
    {
        get => _extension;
        private set { if (_extension == value) return; _extension = value; OnPropertyChanged(); }
    }
    public long SizeBytes
    {
        get => _sizeBytes;
        private set { if (_sizeBytes == value) return; _sizeBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(SizeDisplay)); }
    }
    public DateTime ModifiedDate
    {
        get => _modifiedDate;
        private set { if (_modifiedDate == value) return; _modifiedDate = value; OnPropertyChanged(); }
    }
    public MediaKind Kind { get; }

    /// <summary>
    /// Update path/name/size after a rename has been performed on disk. Caller must
    /// have already moved the file. Triggers PropertyChanged on bindings so list
    /// rows refresh in place.
    /// </summary>
    public void UpdateAfterRename(string newFullPath)
    {
        FullPath = newFullPath;
        var fi = new FileInfo(newFullPath);
        FileName = fi.Name;
        Extension = fi.Extension.ToLowerInvariant();
        if (fi.Exists)
        {
            SizeBytes = fi.Length;
            ModifiedDate = fi.LastWriteTime;
        }
    }

    public string SizeDisplay => FormatSize(SizeBytes);

    public System.Windows.Media.Imaging.BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public int PixelWidth
    {
        get => _pixelWidth;
        set
        {
            if (_pixelWidth == value) return;
            _pixelWidth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AspectDisplay));
        }
    }

    public int PixelHeight
    {
        get => _pixelHeight;
        set
        {
            if (_pixelHeight == value) return;
            _pixelHeight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AspectDisplay));
        }
    }

    /// <summary>Friendly aspect-ratio label, e.g. "Landscape 16:9" or "Portrait 9:16".</summary>
    public string AspectDisplay => FormatAspect(PixelWidth, PixelHeight);

    /// <summary>Video duration in seconds. 0 for images or unknown.</summary>
    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (_durationSeconds == value) return;
            _durationSeconds = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationDisplay));
        }
    }

    public string DurationDisplay
    {
        get
        {
            if (DurationSeconds <= 0) return "";
            var t = TimeSpan.FromSeconds(DurationSeconds);
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{t.Minutes}:{t.Seconds:00}";
        }
    }

    /// <summary>Aspect bucket: Portrait, Landscape, Square, or Unknown.</summary>
    public string AspectBucket
    {
        get
        {
            if (PixelWidth <= 0 || PixelHeight <= 0) return "Unknown";
            var r = (double)PixelWidth / PixelHeight;
            if (Math.Abs(r - 1.0) < 0.05) return "Square";
            return r > 1.0 ? "Landscape" : "Portrait";
        }
    }

    /// <summary>Perceptual hash string (set by duplicate detector).</summary>
    public string PerceptualHash
    {
        get => _perceptualHash;
        set { _perceptualHash = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>True if duplicate detector flagged this item.</summary>
    public bool IsDuplicate
    {
        get => _isDuplicate;
        set { _isDuplicate = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Whether this item is currently selected in the source list. Bound two-way to the
    /// container's IsSelected via ItemContainerStyle, so selection survives container
    /// recycling (virtualization, thumbnail-loaded re-layout, sort updates, etc.).
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// User-toggled favorite/star marker. Persisted across sessions in AppSettings.Favorites.
    /// Press F to toggle on the selection.
    /// </summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StarGlyph));
        }
    }

    /// <summary>Star glyph for binding (★ when favorited, empty otherwise).</summary>
    public string StarGlyph => IsFavorite ? "★" : "";

    public MediaItem(string fullPath)
    {
        _fullPath = fullPath;
        var fi = new FileInfo(fullPath);
        _fileName = fi.Name;
        _extension = fi.Extension.ToLowerInvariant();
        _sizeBytes = fi.Exists ? fi.Length : 0;
        _modifiedDate = fi.Exists ? fi.LastWriteTime : DateTime.MinValue;
        Kind = ClassifyExtension(_extension);
    }

    public static MediaKind ClassifyExtension(string ext)
    {
        ext = ext.ToLowerInvariant();
        if (MediaFormats.ImageExtensions.Contains(ext)) return MediaKind.Image;
        if (MediaFormats.VideoExtensions.Contains(ext)) return MediaKind.Video;
        return MediaKind.Other;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.##} {units[u]}";
    }

    private static string FormatAspect(int w, int h)
    {
        if (w <= 0 || h <= 0) return "";

        double ratio = (double)w / h;

        // Snap to common aspect ratios with a small tolerance.
        // Order matters: more specific labels first.
        (string label, double target)[] candidates =
        {
            ("Square 1:1",       1.0),
            ("Portrait 9:16",    9.0  / 16.0),
            ("Portrait 3:4",     3.0  / 4.0),
            ("Portrait 2:3",     2.0  / 3.0),
            ("Portrait 4:5",     4.0  / 5.0),
            ("Landscape 16:9",   16.0 / 9.0),
            ("Landscape 4:3",    4.0  / 3.0),
            ("Landscape 3:2",    3.0  / 2.0),
            ("Landscape 5:4",    5.0  / 4.0),
            ("Landscape 21:9",   21.0 / 9.0),
            ("Landscape 2:1",    2.0),
        };

        foreach (var (label, target) in candidates)
        {
            // ~3% tolerance — covers 1920x1080 vs 1920x1088 etc.
            if (Math.Abs(ratio - target) / target < 0.03) return label;
        }

        // Fallback: "Portrait 5:7" / "Landscape 7:5" using gcd reduction.
        var orientation = ratio > 1.0 ? "Landscape" : "Portrait";
        var g = Gcd(w, h);
        var rw = w / g;
        var rh = h / g;
        // If reduced ratio is silly (e.g. 1234:567), just show 1 decimal ratio.
        if (rw > 30 || rh > 30)
        {
            return ratio > 1.0
                ? $"{orientation} {ratio:0.##}:1"
                : $"{orientation} 1:{(1.0 / ratio):0.##}";
        }
        return $"{orientation} {rw}:{rh}";
    }

    private static int Gcd(int a, int b)
    {
        a = Math.Abs(a); b = Math.Abs(b);
        while (b != 0) { var t = b; b = a % b; a = t; }
        return a == 0 ? 1 : a;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
