using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaSort.Services;

/// <summary>
/// Rotates an image file in 90° increments and writes it back to disk.
///
/// Strategy: decode → re-encode with the format's native encoder. For JPEG
/// this is a re-encode at quality 95 (one-time slight loss, but works for
/// any rotation angle and any JPEG variant including those without orientation
/// metadata); for PNG/BMP/GIF/TIFF/WebP fallback we re-encode losslessly.
///
/// We write to a temp file in the same folder, then File.Replace the original.
/// This keeps the operation atomic — if the encode fails halfway through, the
/// original file is untouched.
///
/// Returns true on success. The caller is responsible for refreshing the
/// thumbnail cache (the file's mtime changes, so the cache key changes
/// automatically).
/// </summary>
public static class ImageRotator
{
    public enum RotationDirection { Clockwise90, Counterclockwise90, Rotate180 }

    public static bool Rotate(string fullPath, RotationDirection direction, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(fullPath))
            {
                error = "File not found.";
                return false;
            }

            // Decode the image fully into memory before we touch the file system —
            // OnLoad lets us close the stream immediately so we can overwrite later.
            BitmapSource source;
            BitmapDecoder decoder;
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                decoder = BitmapDecoder.Create(
                    fs,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                {
                    error = "Image has no frames.";
                    return false;
                }
                source = decoder.Frames[0];
            }

            double angle = direction switch
            {
                RotationDirection.Clockwise90 => 90,
                RotationDirection.Counterclockwise90 => -90,
                RotationDirection.Rotate180 => 180,
                _ => 0
            };

            var rotated = new TransformedBitmap(source, new RotateTransform(angle));
            // Freeze so it can be encoded from any thread. Rotated bitmaps don't
            // freeze automatically because TransformedBitmap is a reference to
            // its source, but we only use it once before encoding.
            if (rotated.CanFreeze) rotated.Freeze();

            // Pick the encoder that matches the file extension.
            BitmapEncoder encoder = PickEncoder(fullPath);
            encoder.Frames.Add(BitmapFrame.Create(rotated));

            // Atomic write: temp file → File.Replace. If the encode throws part
            // way through, we never overwrite the original.
            var dir = Path.GetDirectoryName(fullPath) ?? ".";
            var tmp = Path.Combine(dir, "." + Path.GetFileName(fullPath) + ".rot.tmp");
            try
            {
                using (var ofs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(ofs);
                }
                // File.Replace preserves ACLs and makes the rename atomic on NTFS.
                // On UNC paths it may fall back to copy+delete; that's still correct.
                try
                {
                    File.Replace(tmp, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    // Fallback for filesystems that don't support Replace (rare).
                    File.Copy(tmp, fullPath, overwrite: true);
                    File.Delete(tmp);
                }
                catch (IOException)
                {
                    // Some network filesystems reject Replace; copy-overwrite as a fallback.
                    File.Copy(tmp, fullPath, overwrite: true);
                    File.Delete(tmp);
                }
            }
            finally
            {
                // Best-effort cleanup if the temp is still around (e.g. encode threw).
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static BitmapEncoder PickEncoder(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".png" => new PngBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            ".tif" or ".tiff" => new TiffBitmapEncoder(),
            ".wmp" => new WmpBitmapEncoder(),
            // WebP/HEIC/RAW etc.: WPF can't re-encode these directly. Fall back
            // to PNG which always works; the caller can warn the user.
            _ => new PngBitmapEncoder(),
        };
    }

    /// <summary>True if the extension is one we can rotate in place. WebP/HEIC/RAW
    /// would silently get re-encoded as PNG by PickEncoder, which would surprise
    /// the user, so we return false for those and the caller hides the menu items.</summary>
    public static bool CanRotate(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".wmp";
    }
}
