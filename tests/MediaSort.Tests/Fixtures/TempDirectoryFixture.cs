// Disposable temp-directory fixture for filesystem-touching tests.
//
// xUnit creates one instance per test class when used as IClassFixture<T>.
// The Dispose method best-effort-cleans the directory after the class's
// tests finish so we don't leave junk under %TEMP%\MediaSort.Tests\.

using System;
using System.IO;

namespace MediaSort.Tests.Fixtures;

public sealed class TempDirectoryFixture : IDisposable
{
    public string Root { get; }

    public TempDirectoryFixture()
    {
        // Single root per fixture instance; tests inside the class get
        // subdirectories under this root via CreateScope() so they don't
        // collide with each other.
        Root = Path.Combine(Path.GetTempPath(), "MediaSort.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>
    /// Create a fresh subdirectory under <see cref="Root"/> with a unique name.
    /// Caller does not need to clean up -- the whole fixture is recursively
    /// deleted on Dispose.
    /// </summary>
    public string CreateScope(string label = "scope")
    {
        var dir = Path.Combine(Root, $"{label}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best effort -- on Windows the temp tree can be locked by a
            // virus scanner mid-teardown. Worst case we leak a small dir
            // under %TEMP%; the OS cleans %TEMP% on its own schedule.
        }
    }
}
