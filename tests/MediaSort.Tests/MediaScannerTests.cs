// Tests for MediaSort.Services.MediaScanner.ScanFast.
//
// The Recursive checkbox is one of the most user-visible toggles in
// MediaSort. v1.0.166 fixed a bug where the Settings dialog wouldn't
// persist a flip of this flag. These tests guard the scanner-side
// half of that contract: when recursive is true, subfolder files are
// found; when false, they aren't. Plus the hidden-file filter so we
// don't accidentally start surfacing $RECYCLE.BIN contents.

using System.IO;
using System.Linq;
using System.Threading;
using MediaSort.Services;
using MediaSort.Tests.Fixtures;
using Xunit;

namespace MediaSort.Tests;

public sealed class MediaScannerTests : IClassFixture<TempDirectoryFixture>
{
    private readonly TempDirectoryFixture _fx;

    public MediaScannerTests(TempDirectoryFixture fx) => _fx = fx;

    /// <summary>
    /// Build a tree like:
    ///   root/
    ///     a.jpg
    ///     b.png
    ///     notes.txt         (non-media, should be skipped)
    ///     sub1/
    ///       c.jpeg
    ///       d.mp4
    ///     sub1/sub2/
    ///       e.heic
    /// Returns the root path.
    /// </summary>
    private string BuildMediaTree()
    {
        var root = _fx.CreateScope("scan");
        File.WriteAllBytes(Path.Combine(root, "a.jpg"), [0xFF, 0xD8]);
        File.WriteAllBytes(Path.Combine(root, "b.png"), [0x89, 0x50]);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "not media");
        var sub1 = Path.Combine(root, "sub1");
        Directory.CreateDirectory(sub1);
        File.WriteAllBytes(Path.Combine(sub1, "c.jpeg"), [0xFF, 0xD8]);
        File.WriteAllBytes(Path.Combine(sub1, "d.mp4"), [0x00, 0x00]);
        var sub2 = Path.Combine(sub1, "sub2");
        Directory.CreateDirectory(sub2);
        File.WriteAllBytes(Path.Combine(sub2, "e.heic"), [0x00, 0x00]);
        return root;
    }

    [Fact]
    public void ScanFast_NonRecursive_ReturnsOnlyTopLevelMediaFiles()
    {
        var root = BuildMediaTree();

        var items = MediaScanner.ScanFast(root, recursive: false, includeHidden: false, CancellationToken.None)
                                .ToList();

        // Expect a.jpg + b.png; reject notes.txt and everything in sub1/.
        var names = items.Select(i => Path.GetFileName(i.FullPath)).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "a.jpg", "b.png" }, names);
    }

    [Fact]
    public void ScanFast_Recursive_ReturnsAllNestedMediaFiles()
    {
        var root = BuildMediaTree();

        var items = MediaScanner.ScanFast(root, recursive: true, includeHidden: false, CancellationToken.None)
                                .ToList();

        // All five media files. notes.txt still rejected by extension filter.
        var names = items.Select(i => Path.GetFileName(i.FullPath)).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "a.jpg", "b.png", "c.jpeg", "d.mp4", "e.heic" }, names);
    }

    [Fact]
    public void ScanFast_NonExistentFolder_ReturnsEmpty()
    {
        var bogus = Path.Combine(_fx.Root, "does-not-exist");

        var items = MediaScanner.ScanFast(bogus, recursive: true, includeHidden: false, CancellationToken.None)
                                .ToList();

        Assert.Empty(items);
    }

    [Fact]
    public void ScanFast_HiddenFile_ExcludedByDefault()
    {
        // Hidden files matter because Windows littering things like
        // Thumbs.db, desktop.ini, and $RECYCLE.BIN contents in the
        // source tree would otherwise pollute the source list.
        // Linux test runners can't set the Hidden attribute, so this
        // test is Windows-only via [Fact(Skip=...)] on non-Windows.
        if (!OperatingSystem.IsWindows())
            return; // Skip on Linux/Mac CI.

        var root = _fx.CreateScope("hidden");
        var visible = Path.Combine(root, "visible.jpg");
        var hidden = Path.Combine(root, "hidden.jpg");
        File.WriteAllBytes(visible, [0xFF]);
        File.WriteAllBytes(hidden, [0xFF]);
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var defaultScan = MediaScanner.ScanFast(root, recursive: false, includeHidden: false, CancellationToken.None)
                                       .Select(i => Path.GetFileName(i.FullPath))
                                       .ToArray();
        Assert.Equal(new[] { "visible.jpg" }, defaultScan);

        var inclusiveScan = MediaScanner.ScanFast(root, recursive: false, includeHidden: true, CancellationToken.None)
                                         .Select(i => Path.GetFileName(i.FullPath))
                                         .OrderBy(s => s)
                                         .ToArray();
        Assert.Equal(new[] { "hidden.jpg", "visible.jpg" }, inclusiveScan);
    }
}
