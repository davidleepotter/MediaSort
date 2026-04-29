using System;
using System.IO;
using System.Text.RegularExpressions;

namespace MediaSort.Services;

public enum ConflictPolicy
{
    /// <summary>Append " (1)", " (2)" etc. to the filename until unique.</summary>
    Rename,
    /// <summary>Overwrite the existing file at the destination.</summary>
    Overwrite,
    /// <summary>Skip the move (caller should treat as no-op).</summary>
    Skip,
    /// <summary>Ask the caller — surfaced via MoveResult.NeedsUserDecision.</summary>
    Prompt
}

public enum MoveOutcome
{
    Moved,
    Skipped,
    NeedsUserDecision,
    Failed
}

public class MoveResult
{
    public MoveOutcome Outcome { get; set; }
    public string FinalPath { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string? ErrorMessage { get; set; }
}

public static class FileMover
{
    /// <summary>
    /// Legacy convenience: move a file and auto-rename on conflict. Returns final path.
    /// </summary>
    public static string MoveToFolder(string sourceFile, string destFolder)
    {
        var r = MoveToFolder(sourceFile, destFolder, ConflictPolicy.Rename, null);
        if (r.Outcome != MoveOutcome.Moved) throw new IOException(r.ErrorMessage ?? "Move failed");
        return r.FinalPath;
    }

    /// <summary>
    /// Move a file with explicit conflict policy and optional rename template.
    /// Template tokens: {name} (original stem), {ext}, {date:yyyyMMdd}, {counter:000}.
    /// </summary>
    public static MoveResult MoveToFolder(string sourceFile, string destFolder,
                                          ConflictPolicy policy,
                                          string? renameTemplate)
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

            var fileName = ApplyRenameTemplate(sourceFile, renameTemplate);
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
                        target = MakeUniquePath(target);
                        break;
                }
            }

            File.Move(sourceFile, target);
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

    /// <summary>
    /// Copy a file with explicit conflict policy and optional rename template.
    /// Mirrors MoveToFolder semantics but leaves the source file in place.
    /// </summary>
    public static MoveResult CopyToFolder(string sourceFile, string destFolder,
                                          ConflictPolicy policy,
                                          string? renameTemplate)
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

            var fileName = ApplyRenameTemplate(sourceFile, renameTemplate);
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
                        // File.Copy(overwrite:true) handles this in one call
                        break;

                    case ConflictPolicy.Rename:
                    default:
                        target = MakeUniquePath(target);
                        break;
                }
            }

            File.Copy(sourceFile, target, overwrite: policy == ConflictPolicy.Overwrite);
            result.Outcome = MoveOutcome.Moved; // "Moved" outcome reused for "completed successfully"
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

    /// <summary>
    /// Move a file back to its original location (used by Undo). If the original
    /// folder no longer exists, it's recreated. If the original name is taken,
    /// a unique name is generated next to it.
    /// </summary>
    public static MoveResult UndoMove(string currentPath, string originalPath)
    {
        var result = new MoveResult { OriginalPath = currentPath };

        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
            result.Outcome = MoveOutcome.Failed;
            result.ErrorMessage = $"File no longer exists at {currentPath}.";
            return result;
        }

        try
        {
            var origDir = Path.GetDirectoryName(originalPath) ?? "";
            if (!string.IsNullOrWhiteSpace(origDir)) Directory.CreateDirectory(origDir);

            var target = originalPath;
            if (File.Exists(target)) target = MakeUniquePath(target);

            File.Move(currentPath, target);
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

    /// <summary>
    /// Send a file to the Recycle Bin (Windows). Returns true on success.
    /// </summary>
    public static bool SendToRecycleBin(string path)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string MakeUniquePath(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var ext = Path.GetExtension(fullPath);
        int n = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            n++;
        } while (File.Exists(candidate));
        return candidate;
    }

    private static string ApplyRenameTemplate(string sourceFile, string? template)
    {
        var fileName = Path.GetFileName(sourceFile);
        if (string.IsNullOrWhiteSpace(template)) return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var fi = new FileInfo(sourceFile);
        var date = fi.Exists ? fi.LastWriteTime : DateTime.Now;

        // Supported tokens:
        //   {name}                  -> original stem
        //   {ext}                   -> extension w/ dot
        //   {date}                  -> yyyy-MM-dd
        //   {date:FORMAT}           -> custom DateTime format
        //   {counter} / {counter:N} -> reserved (handled by caller for batch)
        string Replace(Match m)
        {
            var token = m.Groups[1].Value;
            var fmt = m.Groups[2].Success ? m.Groups[2].Value : null;
            return token.ToLowerInvariant() switch
            {
                "name"    => stem,
                "ext"     => ext,
                "date"    => date.ToString(string.IsNullOrEmpty(fmt) ? "yyyy-MM-dd" : fmt),
                "counter" => "{counter}" + (fmt != null ? ":" + fmt : ""), // pass-through
                _         => m.Value
            };
        }

        var result = Regex.Replace(template, @"\{(\w+)(?::([^}]+))?\}", Replace);
        // Ensure extension is present
        if (string.IsNullOrEmpty(Path.GetExtension(result))) result += ext;
        // Strip path-illegal chars
        foreach (var c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
        return result;
    }
}
