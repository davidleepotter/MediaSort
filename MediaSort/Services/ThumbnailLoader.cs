using System;
using System.IO;
using System.Windows.Media.Imaging;
using MediaSort.Models;
using SDImage = System.Drawing.Image;
using SDBitmap = System.Drawing.Bitmap;
using SDImageFormat = System.Drawing.Imaging.ImageFormat;

namespace MediaSort.Services;

public static class ThumbnailLoader
{
    public static BitmapImage? LoadThumbnail(MediaItem item, int decodePixelWidth = 128)
    {
        if (item.Kind != MediaKind.Image) return null;

        // RAW: try embedded JPEG thumbnail first (instant, no codec required).
        var ext = System.IO.Path.GetExtension(item.FullPath);
        if (MediaFormats.IsRaw(ext))
        {
            var raw = TryLoadEmbeddedRawThumbnail(item.FullPath, decodePixelWidth);
            if (raw != null) return raw;
            // Fall through to WIC — will only succeed if the user has the
            // Microsoft RAW Image Extension or a vendor codec installed.
        }

        return TryLoadBitmap(item.FullPath, decodePixelWidth);
    }

    /// <summary>
    /// (#15) Pull the embedded JPEG preview out of a RAW file via WIC. RAW files
    /// universally embed a full-size or medium JPEG that decodes instantly without
    /// the Microsoft RAW Image Extension. Returns null if no embedded thumbnail.
    /// </summary>
    public static BitmapImage? TryLoadEmbeddedRawThumbnail(string path, int decodePixelWidth)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                fs,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames[0];
            BitmapSource? thumb = frame.Thumbnail;
            if (thumb == null) return null;

            using var outMs = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(thumb));
            encoder.Save(outMs);
            outMs.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
            bmp.StreamSource = outMs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp.PixelWidth > 0 ? bmp : null;
        }
        catch (Exception ex)
        {
            CrashLogger.Info($"raw-thumb-fail {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Read the bytes of a file on a background thread (cheap), so the caller can
    /// then build the BitmapImage on the UI thread. This avoids cross-thread
    /// freeze/dispatcher quirks that have been observed producing blank thumbnails.
    /// </summary>
    public static byte[]? TryReadAllBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch { return null; }
    }

    /// <summary>
    /// Build a thumbnail BitmapSource from in-memory bytes. Safe to call from a
    /// background thread — we Freeze the result. Returns BitmapSource (not BitmapImage)
    /// because BitmapFrame freezes cross-thread cleanly while BitmapImage often does not.
    /// </summary>
    public static BitmapSource? BitmapFromBytes(byte[] bytes, int decodePixelWidth)
    {
        // Strategy 1: BitmapDecoder → BitmapFrame → optional scale via TransformedBitmap.
        // BitmapFrame is the most permissive cross-thread image source in WPF.
        try
        {
            var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                ms,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                CrashLogger.Info($"thumb-strategy1: no frames");
                return null;
            }
            var frame = decoder.Frames[0];
            BitmapSource result = frame;

            if (decodePixelWidth > 0 && frame.PixelWidth > decodePixelWidth)
            {
                var scale = (double)decodePixelWidth / frame.PixelWidth;
                var tb = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
                tb.Freeze();
                result = tb;
            }
            else
            {
                if (result.CanFreeze) result.Freeze();
            }

            if (result.PixelWidth > 0) return result;
            CrashLogger.Info($"thumb-strategy1: zero dims after decode");
        }
        catch (Exception ex)
        {
            CrashLogger.Info($"thumb-strategy1-fail {ex.GetType().Name}: {ex.Message}");
        }

        // Strategy 2: BitmapImage with full options. May fail on PNGs with weird
        // color profile chunks; we skip DecodePixelWidth to avoid the black-image bug.
        try
        {
            var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            if (bmp.PixelWidth > 0) return bmp;
        }
        catch (Exception ex)
        {
            CrashLogger.Info($"thumb-strategy2-fail {ex.GetType().Name}: {ex.Message}");
        }

        // Strategy 3: GDI+ (System.Drawing) decode → re-encode as plain PNG via WIC.
        // GDI+ uses a completely different codec stack from WPF/WIC and handles many
        // AI-generated PNGs (e.g. VeniceAI, Midjourney) whose unusual iCCP / iTXt /
        // pHYs chunks cause WIC to throw NotSupportedException → ArgumentNullException(key).
        try
        {
            using var inMs = new MemoryStream(bytes);
            using var gdiImg = SDImage.FromStream(inMs, useEmbeddedColorManagement: false, validateImageData: false);
            int srcW = gdiImg.Width;
            int srcH = gdiImg.Height;
            if (srcW <= 0 || srcH <= 0)
            {
                CrashLogger.Info("thumb-strategy3: zero dims from gdi");
                return null;
            }

            int targetW = decodePixelWidth > 0 && srcW > decodePixelWidth ? decodePixelWidth : srcW;
            int targetH = (int)Math.Max(1, Math.Round(srcH * (double)targetW / srcW));

            using var resized = new SDBitmap(targetW, targetH);
            using (var g = System.Drawing.Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(gdiImg, 0, 0, targetW, targetH);
            }

            using var outMs = new MemoryStream();
            resized.Save(outMs, SDImageFormat.Png);
            outMs.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = outMs;
            bmp.EndInit();
            bmp.Freeze();
            if (bmp.PixelWidth > 0) return bmp;
        }
        catch (Exception ex)
        {
            CrashLogger.Info($"thumb-strategy3-fail {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Cheap metadata-only read of an image's pixel dimensions.
    /// Returns (0,0) on failure.
    /// </summary>
    public static (int width, int height) TryReadImageDimensions(string path)
    {
        // First try WIC (cheap, metadata-only, no full pixel read).
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                fs,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            if (decoder.Frames.Count > 0)
            {
                var frame = decoder.Frames[0];
                if (frame.PixelWidth > 0 && frame.PixelHeight > 0)
                    return (frame.PixelWidth, frame.PixelHeight);
            }
        }
        catch { /* fall through to GDI+ */ }

        // Fall back to GDI+ which handles AI-generated PNGs WIC chokes on.
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var img = SDImage.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);
            return (img.Width, img.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    public static BitmapSource? LoadFull(string path)
    {
        // (#15) For RAW files, prefer the embedded full-size JPEG preview — it
        // matches what every camera app shows and avoids the slow / codec-gated
        // RAW pixel decode. Fall through to WIC if no embedded preview.
        var ext = System.IO.Path.GetExtension(path);
        if (MediaFormats.IsRaw(ext))
        {
            var emb = TryLoadEmbeddedRawThumbnail(path, 0);
            if (emb != null) return emb;
        }

        // Try the simple path first
        var simple = TryLoadBitmap(path, 0);
        if (simple != null) return simple;

        // Fall back to BitmapDecoder which is more permissive (handles weird color profiles,
        // lossy ICC chunks, etc. that BitmapImage rejects)
        return TryLoadWithDecoder(path);
    }

    private static BitmapImage? TryLoadBitmap(string path, int decodePixelWidth)
    {
        // Read the file into memory first. Using a MemoryStream avoids a known WPF
        // quirk where StreamSource + DecodePixelWidth can silently produce a black
        // bitmap on some JPGs, and removes any chance of the file handle closing
        // before the decoder finishes.
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            // Fall back to BitmapDecoder for files BitmapImage rejects.
            return TryLoadViaDecoder(path, decodePixelWidth);
        }
    }

    private static BitmapImage? TryLoadViaDecoder(string path, int decodePixelWidth)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                fs,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames[0];

            // Re-encode as PNG into memory, then load that as a BitmapImage so the
            // caller still gets the BitmapImage type they expect.
            using var outMs = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            encoder.Save(outMs);
            outMs.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
            bmp.StreamSource = outMs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? TryLoadWithDecoder(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                fs,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }
}
