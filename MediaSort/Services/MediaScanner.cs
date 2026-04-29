using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using MediaSort.Models;

namespace MediaSort.Services;

public static class MediaScanner
{
    /// <summary>
    /// Original signature kept for any older call sites — defaults to skipping hidden/system files,
    /// no cancellation.
    /// </summary>
    public static IEnumerable<MediaItem> Scan(string folder, bool recursive)
        => Scan(folder, recursive, includeHidden: false, ct: CancellationToken.None);

    /// <summary>
    /// Walk a folder for media files. Honors a CancellationToken so the UI can abandon a slow
    /// scan when the user picks a different folder, and skips Hidden/System files + directories
    /// by default (Windows .git, $RECYCLE.BIN, AppData, thumbs.db caches, etc).
    /// </summary>
    public static IEnumerable<MediaItem> Scan(string folder,
                                              bool recursive,
                                              bool includeHidden,
                                              CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            yield break;

        // Manual recursion so we can (a) skip hidden/system directories without entering them
        // (huge wins on dirs like AppData) and (b) check the cancellation token between yields.
        var stack = new Stack<string>();
        stack.Push(folder);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();

            // Subdirectories
            if (recursive)
            {
                IEnumerable<string> subs;
                try { subs = Directory.EnumerateDirectories(current); }
                catch { subs = System.Linq.Enumerable.Empty<string>(); }

                foreach (var sub in subs)
                {
                    if (ct.IsCancellationRequested) yield break;
                    if (!includeHidden && IsHiddenOrSystem(sub)) continue;
                    stack.Push(sub);
                }
            }

            // Files in current
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(current); }
            catch { files = System.Linq.Enumerable.Empty<string>(); }

            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) yield break;
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (!MediaFormats.AllExtensions.Contains(ext)) continue;
                if (!includeHidden && IsHiddenOrSystem(f)) continue;
                yield return new MediaItem(f);
            }
        }
    }

    private static bool IsHiddenOrSystem(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch
        {
            // Permission errors etc — treat as hidden so we silently skip.
            return true;
        }
    }

    /// <summary>
    /// (Perf #1) Fast scan using <see cref="FileSystemEnumerable{T}"/> with a
    /// <c>FindTransform</c> that reads <c>FileSystemEntry</c> data (name, size,
    /// last-write time, attributes) directly from the underlying Win32
    /// enumeration record. This avoids a second <c>FileInfo</c> stat per file
    /// (one for the IsHiddenOrSystem check, one in the MediaItem ctor) — the
    /// largest cost on big folders or network shares.
    ///
    /// Logs <c>scan: N files in Xms</c> on completion so regressions are visible.
    /// </summary>
    public static IEnumerable<MediaItem> ScanFast(string folder,
                                                   bool recursive,
                                                   bool includeHidden,
                                                   CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            yield break;

        var sw = Stopwatch.StartNew();
        int count = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = includeHidden
                ? FileAttributes.None
                : FileAttributes.Hidden | FileAttributes.System,
            // Match wins (.NET defaults to platform default which is fine on Win)
            ReturnSpecialDirectories = false,
        };

        // Alternate lookup so we can probe the FrozenSet with a
        // ReadOnlySpan<char> directly — zero allocation per file.
        var extLookup = MediaFormats.AllExtensionsSet.GetAlternateLookup<ReadOnlySpan<char>>();

        // Predicate: only yield files that match a known media extension.
        // Operates on the FileSystemEntry struct so no extra path string is allocated.
        bool ShouldInclude(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory) return false;
            // entry.FileName is a ReadOnlySpan<char> over the native record.
            var name = entry.FileName;
            int dot = name.LastIndexOf('.');
            if (dot < 0 || dot == name.Length - 1) return false;
            // FrozenSet uses OrdinalIgnoreCase, so no lowercase pass needed.
            // Single O(1) hash probe over the span, no string allocation.
            return extLookup.Contains(name[dot..]);
        }

        // Transform: project the FileSystemEntry into a MediaItem using its
        // already-populated metadata. No second FileInfo / stat call.
        MediaItem Transform(ref FileSystemEntry entry)
        {
            var fullPath = entry.ToFullPath();
            var fileName = entry.FileName.ToString();
            int dot = fileName.LastIndexOf('.');
            var extension = dot >= 0 ? fileName.Substring(dot).ToLowerInvariant() : string.Empty;
            return new MediaItem(
                fullPath,
                fileName,
                extension,
                entry.Length,
                entry.LastWriteTimeUtc.LocalDateTime,
                entry.CreationTimeUtc.LocalDateTime);
        }

        var enumerable = new FileSystemEnumerable<MediaItem>(
            folder,
            (ref FileSystemEntry e) => Transform(ref e),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => ShouldInclude(ref e),
        };

        IEnumerator<MediaItem>? enumerator = null;
        try
        {
            try { enumerator = enumerable.GetEnumerator(); }
            catch (Exception ex)
            {
                CrashLogger.Info($"scan: enumerator init failed: {ex.Message}");
                yield break;
            }

            while (true)
            {
                if (ct.IsCancellationRequested) yield break;
                MediaItem? next = null;
                try
                {
                    if (!enumerator.MoveNext()) break;
                    next = enumerator.Current;
                }
                catch (Exception ex)
                {
                    // Permission / locked path mid-walk — log and stop cleanly.
                    CrashLogger.Info($"scan: enumerator faulted: {ex.Message}");
                    yield break;
                }
                count++;
                yield return next!;
            }
        }
        finally
        {
            enumerator?.Dispose();
            sw.Stop();
            CrashLogger.Info($"scan: {count} files in {sw.ElapsedMilliseconds}ms (recursive={recursive})");
        }
    }
}
