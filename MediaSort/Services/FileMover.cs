using System;
using System.IO;

namespace MediaSort.Services;

public static class FileMover
{
    /// <summary>
    /// Moves a file to a destination folder. If the file name already exists in the destination,
    /// appends a numeric suffix. Returns the destination path on success, or throws on failure.
    /// </summary>
    public static string MoveToFolder(string sourceFile, string destFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
            throw new FileNotFoundException("Source file not found.", sourceFile);

        if (string.IsNullOrWhiteSpace(destFolder))
            throw new ArgumentException("Destination folder is empty.");

        Directory.CreateDirectory(destFolder);

        var fileName = Path.GetFileName(sourceFile);
        var target = Path.Combine(destFolder, fileName);

        if (File.Exists(target))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            int n = 1;
            do
            {
                target = Path.Combine(destFolder, $"{stem} ({n}){ext}");
                n++;
            } while (File.Exists(target));
        }

        File.Move(sourceFile, target);
        return target;
    }
}
