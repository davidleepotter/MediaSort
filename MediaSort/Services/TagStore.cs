using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MediaSort.Models;

namespace MediaSort.Services;

/// <summary>
/// Per-file rating + tag persistence (UX #14). Stored as a single JSON file at
/// %LOCALAPPDATA%/MediaSort/tags.json keyed by lowercased full path so
/// rating/tag info survives MediaSort restarts and follows files across
/// scans of the same source folder.
///
/// Notes
///  - Rename: callers must invoke <see cref="RenamePath"/> after moving a
///    file so the entry follows it.
///  - Move/Delete: entries for missing files stay in the JSON until
///    <see cref="Compact"/> is called explicitly (it's never automatic, so a
///    user who rescans an offline drive doesn't lose ratings).
/// </summary>
public class TagStore
{
    private class Entry
    {
        public int Rating { get; set; }
        public List<string> Tags { get; set; } = new();
        public bool IsEmpty => Rating == 0 && (Tags == null || Tags.Count == 0);
    }

    private readonly Dictionary<string, Entry> _byPath =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _dirty;

    private static readonly string StoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaSort");
    private static readonly string StorePath = Path.Combine(StoreDir, "tags.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    public TagStore() { Load(); }

    public int Count
    {
        get { lock (_lock) return _byPath.Count; }
    }

    /// <summary>All distinct tags across the store, sorted, for autocomplete.</summary>
    public List<string> AllTags()
    {
        lock (_lock)
        {
            return _byPath.Values
                .SelectMany(e => e.Tags ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Hydrate a freshly-scanned MediaItem with stored rating/tags. No-op if
    /// no entry exists for the path.</summary>
    public void Hydrate(MediaItem item)
    {
        if (item == null) return;
        lock (_lock)
        {
            if (_byPath.TryGetValue(item.FullPath, out var e))
            {
                item.Rating = e.Rating;
                item.Tags = (e.Tags ?? new List<string>()).ToList();
            }
        }
    }

    /// <summary>Bulk hydrate after scan.</summary>
    public void HydrateAll(IEnumerable<MediaItem> items)
    {
        if (items == null) return;
        lock (_lock)
        {
            foreach (var it in items)
            {
                if (_byPath.TryGetValue(it.FullPath, out var e))
                {
                    it.Rating = e.Rating;
                    it.Tags = (e.Tags ?? new List<string>()).ToList();
                }
            }
        }
    }

    /// <summary>Persist a rating change for one item. 0 clears the rating.</summary>
    public void SetRating(MediaItem item, int rating)
    {
        if (item == null) return;
        rating = Math.Max(0, Math.Min(5, rating));
        lock (_lock)
        {
            var e = GetOrCreate(item.FullPath);
            e.Rating = rating;
            CleanIfEmpty(item.FullPath, e);
            _dirty = true;
        }
        item.Rating = rating;
    }

    /// <summary>Replace the entire tag list for one item.</summary>
    public void SetTags(MediaItem item, IEnumerable<string> tags)
    {
        if (item == null) return;
        var clean = NormalizeTags(tags);
        lock (_lock)
        {
            var e = GetOrCreate(item.FullPath);
            e.Tags = clean;
            CleanIfEmpty(item.FullPath, e);
            _dirty = true;
        }
        item.Tags = new List<string>(clean);
    }

    /// <summary>Add tags to an item (union, case-insensitive).</summary>
    public void AddTags(MediaItem item, IEnumerable<string> toAdd)
    {
        if (item == null) return;
        var add = NormalizeTags(toAdd);
        if (add.Count == 0) return;
        lock (_lock)
        {
            var e = GetOrCreate(item.FullPath);
            var union = new List<string>(e.Tags ?? new List<string>());
            foreach (var t in add)
            {
                if (!union.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                    union.Add(t);
            }
            e.Tags = union;
            _dirty = true;
        }
        // Mirror onto the live binding.
        var live = item.Tags == null ? new List<string>() : new List<string>(item.Tags);
        foreach (var t in add)
        {
            if (!live.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                live.Add(t);
        }
        item.Tags = live;
    }

    /// <summary>Remove tags from an item (case-insensitive).</summary>
    public void RemoveTags(MediaItem item, IEnumerable<string> toRemove)
    {
        if (item == null) return;
        var rm = NormalizeTags(toRemove);
        if (rm.Count == 0) return;
        lock (_lock)
        {
            if (!_byPath.TryGetValue(item.FullPath, out var e)) return;
            e.Tags = (e.Tags ?? new List<string>())
                .Where(t => !rm.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            CleanIfEmpty(item.FullPath, e);
            _dirty = true;
        }
        item.Tags = (item.Tags ?? new List<string>())
            .Where(t => !rm.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>Re-key an entry after a rename or move.</summary>
    public void RenamePath(string oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath)) return;
        if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase)) return;
        lock (_lock)
        {
            if (!_byPath.TryGetValue(oldPath, out var e)) return;
            _byPath.Remove(oldPath);
            _byPath[newPath] = e;
            _dirty = true;
        }
    }

    /// <summary>Drop entries whose file no longer exists. Optional cleanup.</summary>
    public int Compact()
    {
        int dropped = 0;
        lock (_lock)
        {
            var stale = _byPath.Keys.Where(p => !File.Exists(p)).ToList();
            foreach (var p in stale) { _byPath.Remove(p); dropped++; }
            if (dropped > 0) _dirty = true;
        }
        return dropped;
    }

    /// <summary>Save to disk if changed since last save. Cheap if not dirty.</summary>
    public void SaveIfDirty()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(StoreDir);
                var tmp = StorePath + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, _byPath, JsonOpts);
                }
                if (File.Exists(StorePath)) File.Delete(StorePath);
                File.Move(tmp, StorePath);
                _dirty = false;
            }
            catch
            {
                // Best-effort persistence; in-memory state remains usable for the session.
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var json = File.ReadAllText(StorePath);
            if (string.IsNullOrWhiteSpace(json)) return;
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json, JsonOpts);
            if (parsed == null) return;
            lock (_lock)
            {
                _byPath.Clear();
                foreach (var kv in parsed)
                {
                    if (kv.Value == null) continue;
                    if (kv.Value.IsEmpty) continue;
                    _byPath[kv.Key] = kv.Value;
                }
            }
        }
        catch
        {
            // Corrupt/missing — start with an empty store.
        }
    }

    private Entry GetOrCreate(string path)
    {
        if (!_byPath.TryGetValue(path, out var e))
        {
            e = new Entry();
            _byPath[path] = e;
        }
        return e;
    }

    private void CleanIfEmpty(string path, Entry e)
    {
        if (e.IsEmpty) _byPath.Remove(path);
    }

    private static List<string> NormalizeTags(IEnumerable<string>? input)
    {
        if (input == null) return new List<string>();
        return input
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
