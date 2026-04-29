using System.Collections.Generic;

namespace MediaSort.Models;

public static class MediaFormats
{
    /// <summary>Standard / WPF-decodable still-image formats.</summary>
    public static readonly HashSet<string> ImageExtensions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff",
        ".heic", ".heif", ".ico", ".jfif"
    };

    /// <summary>
    /// Camera RAW formats. Treated as images at the model level, but loaders prefer
    /// embedded JPEG thumbnails because full decode requires the Microsoft RAW Image
    /// Extension (or vendor codec) and can be very slow on multi-MB RAW files.
    /// </summary>
    public static readonly HashSet<string> RawExtensions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3",          // Canon
        ".nef", ".nrw",          // Nikon
        ".arw", ".sr2", ".srf", // Sony
        ".dng",                   // Adobe / Pixel / many
        ".orf",                   // Olympus
        ".rw2", ".rwl",          // Panasonic / Leica
        ".raf",                   // Fujifilm
        ".pef",                   // Pentax
        ".srw",                   // Samsung
        ".x3f",                   // Sigma
        ".raw"                    // generic
    };

    public static readonly HashSet<string> VideoExtensions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".flv", ".m4v",
        ".mpg", ".mpeg", ".3gp", ".ts", ".mts"
    };

    /// <summary>HEIF container formats (image and image-sequence).</summary>
    public static readonly HashSet<string> HeifExtensions = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif"
    };

    public static bool IsRaw(string extWithDot) => RawExtensions.Contains(extWithDot);
    public static bool IsHeif(string extWithDot) => HeifExtensions.Contains(extWithDot);

    public static IEnumerable<string> AllExtensions
    {
        get
        {
            foreach (var x in ImageExtensions) yield return x;
            foreach (var x in RawExtensions) yield return x;
            foreach (var x in VideoExtensions) yield return x;
        }
    }
}
