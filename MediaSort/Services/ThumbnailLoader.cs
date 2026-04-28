using System;
using System.IO;
using System.Windows.Media.Imaging;
using MediaSort.Models;

namespace MediaSort.Services;

public static class ThumbnailLoader
{
    public static BitmapImage? LoadThumbnail(MediaItem item, int decodePixelWidth = 128)
    {
        if (item.Kind != MediaKind.Image) return null;
        return TryLoadBitmap(item.FullPath, decodePixelWidth);
    }

    /// <summary>
    /// Cheap metadata-only read of an image's pixel dimensions.
    /// Returns (0,0) on failure.
    /// </summary>
    public static (int width, int height) TryReadImageDimensions(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(
                fs,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return (0, 0);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return (0, 0);
        }
    }

    public static BitmapSource? LoadFull(string path)
    {
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
