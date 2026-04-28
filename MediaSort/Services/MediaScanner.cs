using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaSort.Models;

namespace MediaSort.Services;

public static class MediaScanner
{
    public static IEnumerable<MediaItem> Scan(string folder, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return System.Linq.Enumerable.Empty<MediaItem>();

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var files = Directory
            .EnumerateFiles(folder, "*.*", option)
            .Where(f => MediaFormats.AllExtensions
                .Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => new MediaItem(f))
            .OrderBy(m => m.FileName);

        return files;
    }
}
