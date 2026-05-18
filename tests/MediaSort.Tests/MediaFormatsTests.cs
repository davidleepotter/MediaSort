using MediaSort.Models;
using Xunit;

namespace MediaSort.Tests;

/// <summary>
/// MediaFormats is the single source of truth for "what files MediaSort considers
/// media". These tests freeze the expected extension sets and the case-insensitive
/// lookup contract. The scanner relies on <see cref="MediaFormats.AllExtensionsSet"/>
/// for hot-path filtering, so case-insensitivity is a correctness requirement.
/// </summary>
public class MediaFormatsTests
{
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".gif")]
    [InlineData(".webp")]
    [InlineData(".heic")]
    [InlineData(".bmp")]
    [InlineData(".tif")]
    [InlineData(".tiff")]
    public void Image_extensions_present(string ext)
    {
        Assert.Contains(ext, MediaFormats.ImageExtensions);
        Assert.Contains(ext, MediaFormats.AllExtensionsSet);
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mov")]
    [InlineData(".mkv")]
    [InlineData(".avi")]
    [InlineData(".webm")]
    public void Video_extensions_present(string ext)
    {
        Assert.Contains(ext, MediaFormats.VideoExtensions);
        Assert.Contains(ext, MediaFormats.AllExtensionsSet);
    }

    [Theory]
    [InlineData(".cr2")]
    [InlineData(".cr3")]
    [InlineData(".nef")]
    [InlineData(".arw")]
    [InlineData(".dng")]
    [InlineData(".raf")]
    public void Raw_extensions_present(string ext)
    {
        Assert.Contains(ext, MediaFormats.RawExtensions);
        Assert.True(MediaFormats.IsRaw(ext));
        Assert.Contains(ext, MediaFormats.AllExtensionsSet);
    }

    [Theory]
    [InlineData(".heic")]
    [InlineData(".heif")]
    public void Heif_extensions_present(string ext)
    {
        Assert.Contains(ext, MediaFormats.HeifExtensions);
        Assert.True(MediaFormats.IsHeif(ext));
    }

    [Theory]
    [InlineData(".JPG")]
    [InlineData(".Jpg")]
    [InlineData(".PNG")]
    [InlineData(".HEIC")]
    [InlineData(".MP4")]
    public void Lookups_are_case_insensitive(string ext)
    {
        // The scanner sees raw FileSystemInfo.Extension which preserves case;
        // a Windows file written as photo.JPG must still be classified as media.
        Assert.Contains(ext, MediaFormats.AllExtensionsSet);
        Assert.Contains(ext, MediaFormats.ImageExtensions);
    }

    [Theory]
    [InlineData(".txt")]
    [InlineData(".exe")]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    [InlineData(".zip")]
    [InlineData("")]
    public void Non_media_extensions_excluded(string ext)
    {
        Assert.DoesNotContain(ext, MediaFormats.AllExtensionsSet);
    }

    [Fact]
    public void IsRaw_false_for_jpeg()
    {
        Assert.False(MediaFormats.IsRaw(".jpg"));
        Assert.False(MediaFormats.IsRaw(".png"));
    }

    [Fact]
    public void IsHeif_false_for_non_heif()
    {
        Assert.False(MediaFormats.IsHeif(".jpg"));
        Assert.False(MediaFormats.IsHeif(".cr2"));
    }
}
