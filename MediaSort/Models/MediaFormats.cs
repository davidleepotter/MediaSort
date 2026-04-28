using System.Collections.Generic;

namespace MediaSort.Models;

public static class MediaFormats
{
    public static readonly HashSet<string> ImageExtensions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        ".heic", ".heif", ".ico", ".jfif"
    };

    public static readonly HashSet<string> VideoExtensions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".flv", ".m4v",
        ".mpg", ".mpeg", ".3gp", ".ts", ".mts"
    };

    public static IEnumerable<string> AllExtensions
    {
        get
        {
            foreach (var x in ImageExtensions) yield return x;
            foreach (var x in VideoExtensions) yield return x;
        }
    }
}
