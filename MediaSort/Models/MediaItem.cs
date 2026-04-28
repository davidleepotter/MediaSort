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
    private BitmapImage? _thumbnail;

    public string FullPath { get; }
    public string FileName { get; }
    public string Extension { get; }
    public long SizeBytes { get; }
    public DateTime ModifiedDate { get; }
    public MediaKind Kind { get; }

    public string SizeDisplay => FormatSize(SizeBytes);

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public MediaItem(string fullPath)
    {
        FullPath = fullPath;
        var fi = new FileInfo(fullPath);
        FileName = fi.Name;
        Extension = fi.Extension.ToLowerInvariant();
        SizeBytes = fi.Exists ? fi.Length : 0;
        ModifiedDate = fi.Exists ? fi.LastWriteTime : DateTime.MinValue;
        Kind = ClassifyExtension(Extension);
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

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
