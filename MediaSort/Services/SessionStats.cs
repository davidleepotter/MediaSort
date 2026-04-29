using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MediaSort.Services;

/// <summary>
/// (#11) Per-session running totals of files moved / copied / deleted plus byte totals.
/// "Session" means the lifetime of the running process — counts reset to zero on app
/// launch and on explicit user reset. Bound to the status-bar widget; the "click for
/// breakdown" popup reads the same instance.
///
/// Notifications: every Record* call raises PropertyChanged for the affected counters
/// AND for <see cref="Summary"/> so a single TextBlock binding refreshes automatically.
/// </summary>
public class SessionStats : INotifyPropertyChanged
{
    public DateTime SessionStarted { get; private set; } = DateTime.Now;

    private int _movedCount;
    private long _movedBytes;
    private int _copiedCount;
    private long _copiedBytes;
    private int _deletedCount;
    private long _deletedBytes;

    public int MovedCount    { get => _movedCount;   private set => Set(ref _movedCount, value); }
    public long MovedBytes   { get => _movedBytes;   private set => Set(ref _movedBytes, value); }
    public int CopiedCount   { get => _copiedCount;  private set => Set(ref _copiedCount, value); }
    public long CopiedBytes  { get => _copiedBytes;  private set => Set(ref _copiedBytes, value); }
    public int DeletedCount  { get => _deletedCount; private set => Set(ref _deletedCount, value); }
    public long DeletedBytes { get => _deletedBytes; private set => Set(ref _deletedBytes, value); }

    public int TotalCount => MovedCount + CopiedCount + DeletedCount;
    public long TotalBytes => MovedBytes + CopiedBytes + DeletedBytes;

    public bool IsEmpty => TotalCount == 0;

    /// <summary>Compact one-line summary for the status bar.</summary>
    public string Summary
    {
        get
        {
            if (IsEmpty) return "Session: 0 files";
            // Keep it short: "Session: 12 moved · 3 copied · 1 deleted · 245 MB"
            var parts = new System.Collections.Generic.List<string>(4);
            if (MovedCount > 0)   parts.Add($"{MovedCount} moved");
            if (CopiedCount > 0)  parts.Add($"{CopiedCount} copied");
            if (DeletedCount > 0) parts.Add($"{DeletedCount} deleted");
            parts.Add(FormatBytes(TotalBytes));
            return "Session: " + string.Join(" · ", parts);
        }
    }

    public void RecordMove(long bytes)
    {
        MovedCount++;
        MovedBytes += Math.Max(0, bytes);
        RaiseTotals();
    }

    public void RecordCopy(long bytes)
    {
        CopiedCount++;
        CopiedBytes += Math.Max(0, bytes);
        RaiseTotals();
    }

    public void RecordDelete(long bytes)
    {
        DeletedCount++;
        DeletedBytes += Math.Max(0, bytes);
        RaiseTotals();
    }

    /// <summary>Wipe all counters and restart the session clock.</summary>
    public void Reset()
    {
        _movedCount = _copiedCount = _deletedCount = 0;
        _movedBytes = _copiedBytes = _deletedBytes = 0;
        SessionStarted = DateTime.Now;
        // Raise everything so the bindings catch up.
        OnPropertyChanged(nameof(MovedCount));
        OnPropertyChanged(nameof(MovedBytes));
        OnPropertyChanged(nameof(CopiedCount));
        OnPropertyChanged(nameof(CopiedBytes));
        OnPropertyChanged(nameof(DeletedCount));
        OnPropertyChanged(nameof(DeletedBytes));
        RaiseTotals();
        OnPropertyChanged(nameof(SessionStarted));
    }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    public static string FormatBytes(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double s = b; int i = 0;
        while (s >= 1024 && i < u.Length - 1) { s /= 1024; i++; }
        return $"{s:0.##} {u[i]}";
    }

    // INPC plumbing
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(n);
    }
}
