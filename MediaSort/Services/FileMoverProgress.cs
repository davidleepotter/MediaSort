using System;
using System.IO;
using System.Threading;

namespace MediaSort.Services;

/// <summary>
/// Progress-aware file move/copy. Used for files larger than a threshold so the user
/// gets a progress dialog instead of a frozen UI on slow/cross-volume operations.
/// </summary>
public static class FileMoverProgress
{
    /// <summary>Files larger than this trigger the progress dialog (50 MB).</summary>
    public const long LargeFileThreshold = 50L * 1024 * 1024;

    /// <summary>
    /// (#3) Lower threshold used when source and destination are on different volumes,
    /// where any operation is physically a copy. Default 5 MB — below this the I/O is
    /// fast enough that the bare File.Copy/Move path is fine.
    /// </summary>
    public const long CrossVolumeThreshold = 5L * 1024 * 1024;

    public static MoveResult MoveWithProgress(string sourceFile, string destFolder,
        ConflictPolicy policy, string? renameTemplate,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
        => CopyOrMoveWithProgress(sourceFile, destFolder, policy, renameTemplate, isCopy: false, progress, ct);

    public static MoveResult CopyWithProgress(string sourceFile, string destFolder,
        ConflictPolicy policy, string? renameTemplate,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
        => CopyOrMoveWithProgress(sourceFile, destFolder, policy, renameTemplate, isCopy: true, progress, ct);

    private static MoveResult CopyOrMoveWithProgress(string sourceFile, string destFolder,
        ConflictPolicy policy, string? renameTemplate, bool isCopy,
        IProgress<(long done, long total)>? progress, CancellationToken ct)
    {
        var result = new MoveResult { OriginalPath = sourceFile };

        if (string.IsNullOrWhiteSpace(sourceFile) || !File.Exists(sourceFile))
        {
            result.Outcome = MoveOutcome.Failed;
            result.ErrorMessage = "Source file not found.";
            return result;
        }
        if (string.IsNullOrWhiteSpace(destFolder))
        {
            result.Outcome = MoveOutcome.Failed;
            result.ErrorMessage = "Destination folder is empty.";
            return result;
        }

        try
        {
            Directory.CreateDirectory(destFolder);
            var fileName = FileMover.ApplyRenameTemplatePublic(sourceFile, renameTemplate);
            var target = Path.Combine(destFolder, fileName);

            if (File.Exists(target))
            {
                switch (policy)
                {
                    case ConflictPolicy.Skip:
                        result.Outcome = MoveOutcome.Skipped;
                        result.FinalPath = target;
                        return result;
                    case ConflictPolicy.Prompt:
                        result.Outcome = MoveOutcome.NeedsUserDecision;
                        result.FinalPath = target;
                        return result;
                    case ConflictPolicy.Overwrite:
                        File.Delete(target);
                        break;
                    case ConflictPolicy.Rename:
                    default:
                        target = FileMover.MakeUniquePathPublic(target);
                        break;
                }
            }

            // Same-volume move is a metadata rename — no need to chunk-copy.
            if (!isCopy && string.Equals(
                    Path.GetPathRoot(Path.GetFullPath(sourceFile)),
                    Path.GetPathRoot(Path.GetFullPath(target)),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Move(sourceFile, target);
                progress?.Report((1, 1));
                result.Outcome = MoveOutcome.Moved;
                result.FinalPath = target;
                return result;
            }

            // Cross-volume copy in chunks so we can report progress + honor cancel.
            var fi = new FileInfo(sourceFile);
            long total = fi.Length;
            long done = 0;
            const int bufSize = 1024 * 1024;
            var buffer = new byte[bufSize];

            using (var src = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, bufSize, useAsync: false))
            using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufSize, useAsync: false))
            {
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ct.IsCancellationRequested)
                    {
                        dst.Close();
                        try { File.Delete(target); } catch { }
                        result.Outcome = MoveOutcome.Failed;
                        result.ErrorMessage = "Cancelled by user.";
                        return result;
                    }
                    dst.Write(buffer, 0, read);
                    done += read;
                    progress?.Report((done, total));
                }
            }

            // Preserve timestamps so EXIF dates are stable across the copy.
            try
            {
                File.SetCreationTimeUtc(target, fi.CreationTimeUtc);
                File.SetLastWriteTimeUtc(target, fi.LastWriteTimeUtc);
            }
            catch { /* non-fatal */ }

            if (!isCopy)
            {
                try { File.Delete(sourceFile); } catch (Exception ex)
                {
                    // Copy succeeded but delete failed — the file IS at the destination.
                    // Report as Moved but note it in error.
                    result.Outcome = MoveOutcome.Moved;
                    result.FinalPath = target;
                    result.ErrorMessage = "Copied to destination but could not remove source: " + ex.Message;
                    return result;
                }
            }

            result.Outcome = MoveOutcome.Moved;
            result.FinalPath = target;
            return result;
        }
        catch (Exception ex)
        {
            result.Outcome = MoveOutcome.Failed;
            result.ErrorMessage = ex.Message;
            return result;
        }
    }
}
