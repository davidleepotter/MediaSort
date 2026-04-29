using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace MediaSort.Services;

/// <summary>
/// Reads basic EXIF metadata from JPEG/TIFF/HEIC images using WPF's BitmapMetadata.
/// Returns a friendly key/value list ready to display.
/// </summary>
public static class ExifReader
{
    public static List<KeyValuePair<string, string>> Read(string path)
    {
        var rows = new List<KeyValuePair<string, string>>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return rows;
            var meta = decoder.Frames[0].Metadata as BitmapMetadata;
            if (meta == null) return rows;

            void Add(string label, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    rows.Add(new KeyValuePair<string, string>(label, value));
            }

            Add("Camera", $"{meta.CameraManufacturer} {meta.CameraModel}".Trim());
            Add("Date taken", meta.DateTaken ?? "");
            Add("Title", meta.Title ?? "");
            Add("Subject", meta.Subject ?? "");
            Add("Copyright", meta.Copyright ?? "");

            // EXIF query strings — wrapped in try/catch since not all formats support them
            string? Q(string q)
            {
                try { return meta.GetQuery(q)?.ToString(); }
                catch { return null; }
            }

            Add("Exposure", Q("/app1/ifd/exif/{ushort=33434}") is string exp
                ? FormatRational(exp) + " s" : "");
            Add("F-stop", Q("/app1/ifd/exif/{ushort=33437}") is string fn
                ? "f/" + FormatRational(fn) : "");
            Add("ISO", Q("/app1/ifd/exif/{ushort=34855}") ?? "");
            Add("Focal length", Q("/app1/ifd/exif/{ushort=37386}") is string fl
                ? FormatRational(fl) + " mm" : "");
            Add("Lens", Q("/app1/ifd/exif/{ushort=42036}") ?? "");
            Add("Flash", Q("/app1/ifd/exif/{ushort=37385}") ?? "");

            // GPS — keep simple textual rendering if present
            var gpsLat = Q("/app1/ifd/gps/{ushort=2}");
            var gpsLatRef = Q("/app1/ifd/gps/{ushort=1}");
            var gpsLon = Q("/app1/ifd/gps/{ushort=4}");
            var gpsLonRef = Q("/app1/ifd/gps/{ushort=3}");
            if (!string.IsNullOrEmpty(gpsLat) && !string.IsNullOrEmpty(gpsLon))
                Add("GPS", $"{gpsLat} {gpsLatRef}, {gpsLon} {gpsLonRef}");
        }
        catch
        {
            // Silently no-op for files without parseable metadata
        }
        return rows;
    }

    /// <summary>
    /// Cheap parse of EXIF DateTaken for routing/folder templates.
    /// Returns null on failure (file format unsupported, no metadata, etc.).
    /// </summary>
    public static DateTime? TryGetDateTaken(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return null;
            if (decoder.Frames[0].Metadata is not BitmapMetadata meta) return null;
            var raw = meta.DateTaken;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            // EXIF formats: "yyyy:MM:dd HH:mm:ss" is most common; WPF often returns culture-specific.
            if (DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt;
            if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out dt))
                return dt;
            if (DateTime.TryParse(raw, out dt)) return dt;
        }
        catch
        {
            // not an image, no metadata, file locked, etc.
        }
        return null;
    }

    /// <summary>
    /// Cheap read of a single string-valued EXIF tag (camera, lens, ISO, etc.).
    /// Returns null on failure.
    /// </summary>
    public static string? TryGetTag(string path, string tag)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return null;
            if (decoder.Frames[0].Metadata is not BitmapMetadata meta) return null;
            return tag.ToLowerInvariant() switch
            {
                "camera" => $"{meta.CameraManufacturer} {meta.CameraModel}".Trim(),
                "manufacturer" => meta.CameraManufacturer,
                "model" => meta.CameraModel,
                "iso" => SafeQuery(meta, "/app1/ifd/exif/{ushort=34855}"),
                "lens" => SafeQuery(meta, "/app1/ifd/exif/{ushort=42036}"),
                _ => null
            };
        }
        catch { return null; }
    }

    private static string? SafeQuery(BitmapMetadata meta, string q)
    {
        try { return meta.GetQuery(q)?.ToString(); }
        catch { return null; }
    }

    private static string FormatRational(string s)
    {
        // Many EXIF reads come back as "n/d" or numeric strings. Best-effort tidy.
        if (s.Contains('/'))
        {
            var parts = s.Split('/');
            if (parts.Length == 2 && double.TryParse(parts[0], out var n) && double.TryParse(parts[1], out var d) && d != 0)
                return (n / d).ToString("0.##");
        }
        return s;
    }
}
