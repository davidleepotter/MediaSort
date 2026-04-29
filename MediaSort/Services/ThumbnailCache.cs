using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace MediaSort.Services;

/// <summary>
/// Two-tier thumbnail cache (#2):
///   Tier 1 — in-memory LRU dict (fast, bounded, lost on restart)
///   Tier 2 — PNG sidecar files in %LOCALAPPDATA%\MediaSort\thumbs\&lt;hash&gt;.png
///            keyed by full path + last-write-time + file size, so edits invalidate.
///
/// All images returned are frozen, safe to hand to the UI thread.
/// </summary>
public static class ThumbnailCache
{
    // Bounded memory cache. Beyond MaxMemoryEntries the LRU tail is evicted.
    private const int MaxMemoryEntries = 600;

    // Bumped if the cache key/format changes — older PNG sidecars become misses.
    // v2: video thumbnails now extracted via Shell at 256px; v1 cached generic
    //     video icons must be invalidated.
    private const string CacheVersion = "v2";

    private static readonly object _lock = new();
    private static readonly LinkedList<string> _lru = new();
    private static readonly Dictionary<string, (LinkedListNode<string> node, BitmapSource img)>
        _mem = new(StringComparer.OrdinalIgnoreCase);

    private static string CacheDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MediaSort", "thumbs");
            try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }
            return dir;
        }
    }

    /// <summary>
    /// Build a stable per-file cache key. Encodes path + mtime + size + version + decode width
    /// so editing/replacing the file invalidates the entry, and changing the requested width
    /// invalidates too.
    /// </summary>
    private static string KeyFor(string fullPath, int decodeWidth)
    {
        long size = -1;
        long ticks = -1;
        try
        {
            var fi = new FileInfo(fullPath);
            size = fi.Exists ? fi.Length : -1;
            ticks = fi.Exists ? fi.LastWriteTimeUtc.Ticks : -1;
        }
        catch { /* missing/locked — fall through with -1s */ }

        var raw = $"{CacheVersion}|w{decodeWidth}|{fullPath.ToLowerInvariant()}|{size}|{ticks}";
        // Short, filename-safe SHA1 hex. Collisions don't matter for cache correctness because
        // we re-derive the key on each lookup; worst case is a stale entry overwritten on next
        // miss.
        using var sha = SHA1.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Memory-tier lookup. Returns null on miss.</summary>
    public static BitmapSource? TryGetMemory(string fullPath, int decodeWidth)
    {
        var key = KeyFor(fullPath, decodeWidth);
        lock (_lock)
        {
            if (_mem.TryGetValue(key, out var entry))
            {
                // Promote to MRU.
                _lru.Remove(entry.node);
                _lru.AddFirst(entry.node);
                return entry.img;
            }
        }
        return null;
    }

    /// <summary>Disk-tier lookup. Reads, decodes, freezes, and promotes into memory.</summary>
    public static BitmapSource? TryGetDisk(string fullPath, int decodeWidth)
    {
        var key = KeyFor(fullPath, decodeWidth);
        var path = Path.Combine(CacheDir, key + ".png");
        if (!File.Exists(path)) return null;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                ms,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            BitmapSource frame = decoder.Frames[0];
            if (frame.CanFreeze) frame.Freeze();
            PutMemory(key, frame);
            return frame;
        }
        catch
        {
            // Corrupt sidecar — best-effort delete so we don't keep failing on it.
            try { File.Delete(path); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Persist a freshly-decoded thumbnail into both tiers. PNG-encodes onto disk as a sidecar.
    /// </summary>
    public static void Put(string fullPath, int decodeWidth, BitmapSource thumb)
    {
        var key = KeyFor(fullPath, decodeWidth);
        PutMemory(key, thumb);

        var path = Path.Combine(CacheDir, key + ".png");
        try
        {
            // Write to a temp file then move so partial writes don't poison the cache.
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(thumb));
                encoder.Save(fs);
            }
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            File.Move(tmp, path);
        }
        catch
        {
            // Disk full / antivirus / readonly profile — silently degrade to memory-only.
        }
    }

    private static void PutMemory(string key, BitmapSource img)
    {
        lock (_lock)
        {
            if (_mem.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing.node);
            }
            var node = new LinkedListNode<string>(key);
            _lru.AddFirst(node);
            _mem[key] = (node, img);

            while (_lru.Count > MaxMemoryEntries)
            {
                var tail = _lru.Last;
                if (tail == null) break;
                _lru.RemoveLast();
                _mem.Remove(tail.Value);
            }
        }
    }

    /// <summary>Clear both tiers. Used by Settings → "Clear thumbnail cache".</summary>
    public static void ClearAll()
    {
        lock (_lock)
        {
            _mem.Clear();
            _lru.Clear();
        }
        try
        {
            var dir = CacheDir;
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
                {
                    try { File.Delete(f); } catch { }
                }
                foreach (var f in Directory.EnumerateFiles(dir, "*.tmp"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>(diskBytes, fileCount) — for Settings UI status text.</summary>
    public static (long bytes, int count) GetDiskStats()
    {
        long bytes = 0;
        int count = 0;
        try
        {
            var dir = CacheDir;
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.png"))
                {
                    try { bytes += new FileInfo(f).Length; count++; } catch { }
                }
            }
        }
        catch { }
        return (bytes, count);
    }
}
