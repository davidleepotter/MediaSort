using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
    /// then build the BitmapImage on the UI thread. Kept for legacy callers — the
    /// scan probe path no longer goes through this. Most images on network shares
    /// are large (5–50 MB) and reading them all into byte arrays caused a 60+ GB
    /// working set on a 1938-image share scan.
    /// </summary>
    public static byte[]? TryReadAllBytes(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch { return null; }
    }

    /// <summary>
    /// Decode an image thumbnail directly from disk via FileStream, decoding to the
    /// requested width (BitmapImage.DecodePixelWidth). This avoids ever materializing
    /// the full file bytes, which was causing tens of GB of RAM use on large
    /// network-share scans.
    /// </summary>
    public static BitmapSource? LoadImageThumbnailFromFile(string path, int decodePixelWidth)
    {
        // Strategy A: BitmapImage.UriSource with DecodePixelWidth set BEFORE EndInit.
        // This is the WPF-blessed cheap thumbnail path — WIC reads only the bytes it
        // needs to produce the requested decode width, then closes the file.
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // load and close stream
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            if (bmp.PixelWidth > 0) return bmp;
        }
        catch (Exception ex)
        {
            // Some PNGs (AI-generated, weird iCCP chunks) blow up the WIC path.
            // Fall through to strategy B.
            CrashLogger.Info($"thumb-fileA-fail {ex.GetType().Name}: {ex.Message}");
        }

        // Strategy B: BitmapDecoder over a streaming FileStream. Same cross-thread
        // friendliness as the old in-memory path but without the giant byte buffer.
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: false);
            var decoder = BitmapDecoder.Create(
                fs,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames[0];
            BitmapSource result = frame;
            if (decodePixelWidth > 0 && frame.PixelWidth > decodePixelWidth)
            {
                var scale = (double)decodePixelWidth / frame.PixelWidth;
                var tb = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
                tb.Freeze();
                result = tb;
            }
            else if (result.CanFreeze)
            {
                result.Freeze();
            }
            if (result.PixelWidth > 0) return result;
        }
        catch (Exception ex)
        {
            CrashLogger.Info($"thumb-fileB-fail {ex.GetType().Name}: {ex.Message}");
        }

        // Strategy C: GDI+ fallback for AI-generated PNGs whose unusual chunks
        // crash WIC. We DO need to read the whole file here, but only as a last
        // resort — most files succeed via A or B.
        try
        {
            using var inFs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var gdiImg = SDImage.FromStream(inFs, useEmbeddedColorManagement: false, validateImageData: false);
            int srcW = gdiImg.Width;
            int srcH = gdiImg.Height;
            if (srcW <= 0 || srcH <= 0) return null;

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
            CrashLogger.Info($"thumb-fileC-fail {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Build a thumbnail BitmapSource from in-memory bytes. Kept for legacy callers.
    /// Prefer LoadImageThumbnailFromFile which streams from disk.
    /// </summary>
    public static BitmapSource? BitmapFromBytes(byte[] bytes, int decodePixelWidth)
    {
        try
        {
            var ms = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                ms,
                BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            var frame = decoder.Frames[0];
            BitmapSource result = frame;
            if (decodePixelWidth > 0 && frame.PixelWidth > decodePixelWidth)
            {
                var scale = (double)decodePixelWidth / frame.PixelWidth;
                var tb = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
                tb.Freeze();
                result = tb;
            }
            else if (result.CanFreeze)
            {
                result.Freeze();
            }
            if (result.PixelWidth > 0) return result;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Extract a thumbnail for a video (or any file Explorer can produce a preview for)
    /// using the Windows Shell IShellItemImageFactory. This is the same pipeline
    /// Explorer uses, so it works for every codec the OS knows about (built-in or
    /// installed pack) without us hauling in ffmpeg.
    /// Returns a frozen BitmapSource, or null if the shell couldn't produce one.
    /// </summary>
    public static BitmapSource? LoadShellThumbnail(string path, int decodePixelWidth = 256)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        // The Shell IShellItemImageFactory is documented to require an STA apartment
        // when invoked from arbitrary threads. Probe pool threads default to MTA,
        // and the call silently returns nothing for many video file types in MTA.
        // If we're already on STA (UI thread), call directly; otherwise spin up
        // a one-shot STA thread that runs CoInitialize, does the work, returns,
        // and tears itself down.
        if (System.Threading.Thread.CurrentThread.GetApartmentState() == System.Threading.ApartmentState.STA)
            return LoadShellThumbnailCore(path, decodePixelWidth);

        BitmapSource? result = null;
        var t = new System.Threading.Thread(() =>
        {
            try { result = LoadShellThumbnailCore(path, decodePixelWidth); }
            catch (Exception ex) { CrashLogger.Info($"shell-thumb-sta-fail {ex.GetType().Name}: {ex.Message}"); }
        });
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        // 8s upper bound — video thumb providers occasionally pull a frame from
        // a slow codec but should never hang an entire scan.
        t.Join(TimeSpan.FromSeconds(8));
        return result;
    }

    private static BitmapSource? LoadShellThumbnailCore(string path, int decodePixelWidth)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var iidShellItem = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iidShellItem, out var factory);
            if (hr != 0 || factory == null) return null;

            try
            {
                // Ask for a generous size so the Shell extracts a real frame rather
                // than scaling up an icon. We omit MemoryOnly (which forbids extraction)
                // and omit ThumbnailOnly (which forbids icon fallback for unknown
                // formats). The returned image is the same one Explorer would show.
                int size = decodePixelWidth > 0 ? decodePixelWidth : 256;
                if (size < 256) size = 256; // smaller sizes can return cached icons only
                var sz = new SIZE { cx = size, cy = size };
                const SIIGBF flags = SIIGBF.ResizeToFit | SIIGBF.BiggerSizeOk;
                hr = factory.GetImage(sz, flags, out hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                {
                    CrashLogger.Info($"shell-thumb: GetImage hr=0x{hr:X8} ({path})");
                    return null;
                }

                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                if (src == null) return null;
                if (src.CanFreeze) src.Freeze();
                if (src.PixelWidth <= 0) return null;
                CrashLogger.Info($"shell-thumb ok {src.PixelWidth}x{src.PixelHeight} ({System.IO.Path.GetFileName(path)})");
                return src;
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception ex)
        {
            CrashLogger.Info($"shell-thumb-fail {ex.GetType().Name}: {ex.Message} ({path})");
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                try { DeleteObject(hBitmap); } catch { /* best-effort */ }
            }
        }
    }

    // ---- Win32 / Shell interop for LoadShellThumbnail ----

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [Flags]
    private enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
        CropToSquare = 0x20,
        WideThumbnails = 0x40,
        IconBackground = 0x80,
        ScaleUp = 0x100,
        InMemory = MemoryOnly
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
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
