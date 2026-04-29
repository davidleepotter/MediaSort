using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MediaSort.Services;

/// <summary>
/// Difference-hash perceptual hash (dHash). Encodes the gradient between
/// adjacent pixels of a 9x8 grayscale downscale into a 64-bit fingerprint.
/// Near-duplicates differ by Hamming distance &lt;= ~4 bits.
///
/// Why dHash and not aHash? aHash (mean threshold) only knows whether each
/// cell is brighter or darker than the average, so two completely different
/// photos with similar overall brightness distribution — e.g. two sky-heavy
/// shots — collide. dHash measures *change* across the image (edges,
/// gradients), which is much more discriminative for natural photos at the
/// same speed.
/// </summary>
public static class PerceptualHasher
{
    /// <summary>Compute 64-bit dHash and return as 16-char hex. Empty string on failure.</summary>
    public static string Hash(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bmp = BitmapDecoder.Create(fs,
                BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames[0];

            // Scale to 9x8 grayscale: 9 columns gives us 8 horizontal differences
            // per row, and 8 rows = 64 bits total.
            const int targetW = 9;
            const int targetH = 8;
            var scaled = new TransformedBitmap(bmp, new ScaleTransform(
                (double)targetW / bmp.PixelWidth, (double)targetH / bmp.PixelHeight));
            var gray = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);

            int w = gray.PixelWidth, h = gray.PixelHeight;
            if (w < 2 || h <= 0) return "";

            int stride = (w * gray.Format.BitsPerPixel + 7) / 8;
            byte[] pixels = new byte[h * stride];
            gray.CopyPixels(pixels, stride, 0);

            // Build 64-bit hash from horizontal gradients: bit set if left pixel
            // is brighter than the pixel to its right.
            ulong hash = 0;
            int bit = 0;
            int cols = Math.Min(w - 1, 8);
            int rows = Math.Min(h, 8);
            for (int y = 0; y < rows && bit < 64; y++)
            {
                int rowOffset = y * stride;
                for (int x = 0; x < cols && bit < 64; x++)
                {
                    if (pixels[rowOffset + x] > pixels[rowOffset + x + 1])
                        hash |= (1UL << bit);
                    bit++;
                }
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
