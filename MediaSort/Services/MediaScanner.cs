using System.Collections.Generic;
using System.IO;
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
}
