using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaSort.Services;

/// <summary>
/// Average-hash perceptual hash (aHash). Computes a 64-bit fingerprint of an
/// image; near-duplicates differ by Hamming distance &lt;= ~6 bits.
/// Cheap (8x8 grayscale downscale + threshold) and works for any image format
/// WPF can decode.
/// </summary>
public static class PerceptualHasher
{
    /// <summary>Compute 64-bit aHash and return as 16-char hex. Empty string on failure.</summary>
    public static string Hash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bmp = BitmapDecoder.Create(fs,
                BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames[0];

            // Scale to 8x8 grayscale
            var scaled = new TransformedBitmap(bmp, new ScaleTransform(
                8.0 / bmp.PixelWidth, 8.0 / bmp.PixelHeight));
            var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);

            int w = gray.PixelWidth, h = gray.PixelHeight;
            if (w <= 0 || h <= 0) return "";

            int stride = (w * gray.Format.BitsPerPixel + 7) / 8;
            byte[] pixels = new byte[h * stride];
            gray.CopyPixels(pixels, stride, 0);

            // Compute mean
            long sum = 0;
            int count = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    sum += pixels[y * stride + x];
                    count++;
                }
            byte mean = (byte)(sum / Math.Max(1, count));

            // Build 64-bit hash (or fewer bits if image was tiny)
            ulong hash = 0;
            int bit = 0;
            for (int y = 0; y < h && bit < 64; y++)
                for (int x = 0; x < w && bit < 64; x++)
                {
                    if (pixels[y * stride + x] >= mean) hash |= (1UL << bit);
                    bit++;
                }
            return hash.ToString("x16");
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Hamming distance between two hex hashes. Returns 64 if either is invalid.</summary>
    public static int Distance(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 64;
        if (!ulong.TryParse(a, System.Globalization.NumberStyles.HexNumber, null, out var ua)) return 64;
        if (!ulong.TryParse(b, System.Globalization.NumberStyles.HexNumber, null, out var ub)) return 64;
        return PopCount(ua ^ ub);
    }

    private static int PopCount(ulong v)
    {
        int c = 0;
        while (v != 0) { v &= v - 1; c++; }
        return c;
    }
}
