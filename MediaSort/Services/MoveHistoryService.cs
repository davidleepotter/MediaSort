using System;
using System.Collections.Generic;
using System.IO;

namespace MediaSort.Services;

/// <summary>
/// Tracks recent file moves so the user can undo them. Also writes a CSV
/// audit log to %LOCALAPPDATA%/MediaSort/move-history.csv.
/// </summary>
public class MoveHistoryService
{
    public class MoveRecord
    {
        public string OriginalPath { get; init; } = "";
        public string NewPath { get; init; } = "";
        public DateTime When { get; init; } = DateTime.Now;
        public string Action { get; init; } = "Move"; // Move | Trash
    }

    private readonly Stack<List<MoveRecord>> _undoStack = new();
    private const int MaxUndoBatches = 50;

    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaSort");
    private static readonly string LogPath = Path.Combine(LogDir, "move-history.csv");

    public bool CanUndo => _undoStack.Count > 0;
    public int UndoBatchCount => _undoStack.Count;

    public void Push(List<MoveRecord> batch)
    {
        if (batch == null || batch.Count == 0) return;
        _undoStack.Push(batch);
        while (_undoStack.Count > MaxUndoBatches)
        {
            // Drop oldest by re-stacking — Stack<T> has no easy bottom-trim,
            // so just rebuild without the oldest.
            var keep = new List<List<MoveRecord>>(_undoStack);
            _undoStack.Clear();
            for (int i = keep.Count - 2; i >= 0; i--) _undoStack.Push(keep[i]);
            break;
        }
        AppendCsv(batch);
    }

    public List<MoveRecord>? PopUndo() => _undoStack.Count == 0 ? null : _undoStack.Pop();

    public IEnumerable<List<MoveRecord>> AllBatches() => _undoStack;

    /// <summary>
    /// Remove a specific batch from the stack (used by the History scrubber
    /// when undoing a batch that isn't necessarily the most recent). Identity
    /// match — no value comparison. Returns true if removed.
    /// </summary>
    public bool RemoveBatch(List<MoveRecord> batch)
    {
        if (batch == null || _undoStack.Count == 0) return false;
        // Stack<T> doesn't support arbitrary removal; rebuild without the target.
        var ordered = new List<List<MoveRecord>>(_undoStack); // top -> bottom
        bool removed = ordered.Remove(batch);
        if (!removed) return false;
        _undoStack.Clear();
        // Re-push bottom -> top so the stack order is preserved.
        for (int i = ordered.Count - 1; i >= 0; i--) _undoStack.Push(ordered[i]);
        return true;
    }

    private static void AppendCsv(IEnumerable<MoveRecord> records)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var newFile = !File.Exists(LogPath);
            using var sw = File.AppendText(LogPath);
            if (newFile) sw.WriteLine("Timestamp,Action,OriginalPath,NewPath");
            foreach (var r in records)
            {
                sw.WriteLine($"{r.When:yyyy-MM-dd HH:mm:ss},{r.Action},\"{r.OriginalPath.Replace("\"", "\"\"")}\",\"{r.NewPath.Replace("\"", "\"\"")}\"");
            }
        }
        catch
        {
            // Logging is best-effort; never block a move.
        }
    }

    public static string LogFilePath => LogPath;
}
