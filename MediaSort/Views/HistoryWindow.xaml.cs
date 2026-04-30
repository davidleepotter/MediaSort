using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using MediaSort.Services;

namespace MediaSort.Views;

/// <summary>
/// History scrubber (UX #13). Shows every batch on the undo stack and lets
/// the user undo any one of them, not just the most recent. Backed by
/// MoveHistoryService.AllBatches() / RemoveBatch() and MainWindow.UndoSpecificBatch().
/// </summary>
public partial class HistoryWindow : Window
{
    public class BatchRow
    {
        public List<MoveHistoryService.MoveRecord> Batch { get; init; } = new();
        public string WhenText { get; init; } = "";
        public string ActionText { get; init; } = "";
        public int Count { get; init; }
        public string Summary { get; init; } = "";
    }

    private readonly MainWindow _owner;
    private readonly ObservableCollection<BatchRow> _rows = new();

    public HistoryWindow(MainWindow owner)
    {
        InitializeComponent();
        Owner = owner;
        _owner = owner;
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
        BatchList.ItemsSource = _rows;
        Reload();
    }

    private void Reload()
    {
        _rows.Clear();
        // AllBatches() iterates Stack<T> top-first — that's exactly the order
        // we want to display (most-recent on top).
        foreach (var batch in _owner.HistoryService.AllBatches())
        {
            if (batch.Count == 0) continue;
            var first = batch[0];
            string action = first.Action;
            // Summarize: top destinations + first filename.
            var destFolders = batch
                .Select(r => string.IsNullOrEmpty(r.NewPath) ? "" : Path.GetDirectoryName(r.NewPath) ?? "")
                .Where(s => !string.IsNullOrEmpty(s))
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Take(2)
                .Select(g => $"{Path.GetFileName(g.Key.TrimEnd('\\', '/'))} ({g.Count()})")
                .ToList();
            string firstName = Path.GetFileName(
                string.IsNullOrEmpty(first.NewPath) ? first.OriginalPath : first.NewPath);
            string summary = destFolders.Count > 0
                ? $"{string.Join(", ", destFolders)} — first: {firstName}"
                : $"first: {firstName}";

            _rows.Add(new BatchRow
            {
                Batch = batch,
                WhenText = first.When.ToString("yyyy-MM-dd HH:mm:ss"),
                ActionText = action,
                Count = batch.Count,
                Summary = summary,
            });
        }

        if (_rows.Count == 0)
        {
            // Nice empty-state row. Keep it as a hint — UndoSelected stays disabled.
            DetailList.ItemsSource = null;
            UndoSelectedButton.IsEnabled = false;
        }
        else
        {
            BatchList.SelectedIndex = 0;
        }
    }

    private void BatchList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var row = BatchList.SelectedItem as BatchRow;
        DetailList.ItemsSource = row?.Batch;
        UndoSelectedButton.IsEnabled = row != null && row.Batch.Count > 0
                                       && !string.Equals(row.ActionText, "Trash", StringComparison.OrdinalIgnoreCase);
    }

    private void UndoSelected_Click(object sender, RoutedEventArgs e)
    {
        if (BatchList.SelectedItem is not BatchRow row) return;
        if (string.Equals(row.ActionText, "Trash", StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show(this,
                "Items sent to the Recycle Bin must be restored from Windows.",
                "Cannot undo Trash batch",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }
        _owner.UndoSpecificBatch(row.Batch);
        Reload();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = MoveHistoryService.LogFilePath;
            if (!File.Exists(path))
            {
                System.Windows.MessageBox.Show(this,
                    "No history log file yet. Move some files first.",
                    "Move history", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Could not open log",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_owner.HistoryService.UndoBatchCount == 0)
        {
            System.Windows.MessageBox.Show(this,
                "No batches to clear.",
                "Move history", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }
        var res = System.Windows.MessageBox.Show(this,
            $"Drop all {_owner.HistoryService.UndoBatchCount} undo batch(es) from memory?\n\n" +
            "You will not be able to undo any of them after this. The on-disk CSV audit log is preserved.",
            "Clear move history",
            System.Windows.MessageBoxButton.OKCancel,
            System.Windows.MessageBoxImage.Warning);
        if (res != System.Windows.MessageBoxResult.OK) return;

        _owner.HistoryService.ClearAll();
        _owner.RefreshUndoButtonState();
        Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
