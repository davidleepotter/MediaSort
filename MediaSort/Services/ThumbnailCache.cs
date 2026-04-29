using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace MediaSort.Services;

/// <summary>
/// Two-tier thumbnail cache:
///   Tier 1 — in-memory LRU bounded by BYTES (not entry count). Frozen BitmapSources.
///   Tier 2 — PNG sidecar files in %LOCALAPPDATA%\MediaSort\thumbs\&lt;hash&gt;.png,
///            keyed by full path + last-write-time + size, so edits/replacements
///            invalidate automatically. Disk tier is also byte-bounded with a
///            best-effort LRU sweep based on file last-access time.
///
/// Tuning knobs:
///   MaxMemoryBytes  — soft cap for Tier 1 (default 256 MB)
///   MaxDiskBytes    — soft cap for Tier 2 (default 1 GB)
///   DiskSweepEvery  — minimum interval between disk-tier eviction sweeps
///
/// All images returned are frozen, safe to hand to the UI thread.
/// </summary>
public static class ThumbnailCache
{
    // ----- Tunables -----
    public static long MaxMemoryBytes { get; set; } = 256L * 1024 * 1024;   // 256 MB
    public static long MaxDiskBytes   { get; set; } = 1024L * 1024 * 1024;  // 1 GB
    private static readonly TimeSpan DiskSweepEvery = TimeSpan.FromMinutes(5);

    // Bumped if the cache key/format changes — older PNG sidecars become misses.
    // v2: video thumbnails now extracted via Shell at 256px; v1 cached generic
    //     video icons must be invalidated.
    private const string CacheVersion = "v2";

    // ----- Memory tier state -----
    private static readonly object _lock = new();
    private static readonly LinkedList<string> _lru = new();
    private sealed class MemEntry
    {
        public LinkedListNode<string> Node = null!;
        public BitmapSource Img = null!;
        public long Bytes;
    }
    private static readonly Dictionary<string, MemEntry> _mem =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _memBytes;

    // ----- Disk tier state -----
    private static DateTime _lastDiskSweepUtc = DateTime.MinValue;
    private static readonly object _diskSweepLock = new();

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

    /// <summary>Estimate decoded byte size of a frozen BitmapSource. 4 bytes/pixel is
    /// the common Bgra32 / Pbgra32 case; close enough for a soft byte budget.</summary>
    private static long EstimateBytes(BitmapSource img)
    {
        try
        {
            int bpp = (img.Format.BitsPerPixel <= 0) ? 32 : img.Format.BitsPerPixel;
            long bits = (long)img.PixelWidth * img.PixelHeight * bpp;
            // Add a small overhead per entry for managed wrappers / dict node.
            return (bits / 8) + 256;
        }
        catch
        {
            return 64 * 1024; // 64 KB fallback if dimensions are unavailable.
        }
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
                _lru.Remove(entry.Node);
                _lru.AddFirst(entry.Node);
                return entry.Img;
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

            // Touch access time so disk LRU sweep keeps recently-used entries.
            try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }

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

        MaybeSweepDisk();
    }

    private static void PutMemory(string key, BitmapSource img)
    {
        long bytes = EstimateBytes(img);
        lock (_lock)
        {
            if (_mem.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing.Node);
                _memBytes -= existing.Bytes;
            }
            var node = new LinkedListNode<string>(key);
            _lru.AddFirst(node);
            _mem[key] = new MemEntry { Node = node, Img = img, Bytes = bytes };
            _memBytes += bytes;

            // Evict tail until under byte budget. Keep at least 1 entry so a single
            // oversized thumbnail (rare) is still usable.
            while (_memBytes > MaxMemoryBytes && _lru.Count > 1)
            {
                var tail = _lru.Last;
                if (tail == null) break;
                _lru.RemoveLast();
                if (_mem.TryGetValue(tail.Value, out var tailEntry))
                {
                    _memBytes -= tailEntry.Bytes;
                    _mem.Remove(tail.Value);
                }
            }
        }
    }

    /// <summary>
    /// Throttled disk-tier eviction. Walks the cache dir; if total bytes exceed
    /// MaxDiskBytes, deletes oldest-by-access-time entries until back under cap.
    /// Best-effort, lock-free against memory tier.
    /// </summary>
    private static void MaybeSweepDisk()
    {
        DateTime nowUtc = DateTime.UtcNow;
        lock (_diskSweepLock)
        {
            if (nowUtc - _lastDiskSweepUtc < DiskSweepEvery) return;
            _lastDiskSweepUtc = nowUtc;
        }

        try
        {
            var dir = CacheDir;
            if (!Directory.Exists(dir)) return;
            var files = new DirectoryInfo(dir).GetFiles("*.png");
            long total = 0;
            foreach (var f in files) total += f.Length;
            if (total <= MaxDiskBytes) return;

            // Oldest access first.
            Array.Sort(files, (a, b) => a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc));
            foreach (var f in files)
            {
                if (total <= MaxDiskBytes) break;
                long sz = f.Length;
                try { f.Delete(); total -= sz; } catch { /* skip locked */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Clear both tiers. Used by Settings → "Clear thumbnail cache".</summary>
    public static void ClearAll()
    {
        lock (_lock)
        {
            _mem.Clear();
            _lru.Clear();
            _memBytes = 0;
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

    /// <summary>(memBytes, memCount) — for diagnostics / Settings UI.</summary>
    public static (long bytes, int count) GetMemoryStats()
    {
        lock (_lock)
        {
            return (_memBytes, _mem.Count);
        }
    }

    /// <summary>Combined snapshot for debug logging.</summary>
    public static string GetCacheStats()
    {
        var (mb, mc) = GetMemoryStats();
        var (db, dc) = GetDiskStats();
        return $"thumb-cache mem={mc} entries / {mb / (1024.0 * 1024.0):F1} MB " +
               $"(cap {MaxMemoryBytes / (1024.0 * 1024.0):F0} MB), " +
               $"disk={dc} files / {db / (1024.0 * 1024.0):F1} MB " +
               $"(cap {MaxDiskBytes / (1024.0 * 1024.0):F0} MB)";
    }
}
