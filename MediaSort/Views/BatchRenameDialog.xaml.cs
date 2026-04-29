using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using MediaSort.Models;
using MediaSort.Services;

namespace MediaSort.Views;

public partial class BatchRenameDialog : Window
{
    public class PreviewRow
    {
        public string Original { get; set; } = "";
        public string NewName { get; set; } = "";
        public string Status { get; set; } = "";
        public string FullPath { get; set; } = "";
    }

    private readonly List<MediaItem> _items;
    private bool _ready;

    /// <summary>
    /// After OK, the ordered list of (oldFullPath, newFullPath) renames to perform.
    /// Skips entries whose name didn't change. Empty if cancelled.
    /// </summary>
    public List<(string OldPath, string NewPath)> Plan { get; private set; } = new();

    public BatchRenameDialog(IEnumerable<MediaItem> items)
    {
        InitializeComponent();
        _items = items.ToList();
        HeaderText.Text = $"Renaming {_items.Count} file{(_items.Count == 1 ? "" : "s")}.";
        // Sensible default that keeps the original name and adds a counter only
        // if there would be a conflict — but counter is cheap to add for safety.
        PatternBox.Text = "{name}_{n:000}";

        Loaded += (_, _) =>
        {
            _ready = true;
            PatternBox.Focus();
            PatternBox.SelectAll();
            UpdatePreview();
        };

        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
    }

    private void Pattern_Changed(object sender, RoutedEventArgs e) => UpdatePreview();
    private void Pattern_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        // XAML event handlers fire during InitializeComponent() before all named
        // fields are wired up, so bail until the dialog has fully loaded.
        if (!_ready) return;
        if (PatternBox == null || StartBox == null || PreviewList == null ||
            LowercaseCheck == null || ReplaceSpacesCheck == null || ErrorText == null)
            return;
        ErrorText.Text = "";
        if (!int.TryParse(StartBox.Text?.Trim() ?? "1", out int start)) start = 1;

        var rows = new List<PreviewRow>();
        var newNamesSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = Path.GetInvalidFileNameChars();

        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var dir = Path.GetDirectoryName(item.FullPath) ?? "";
            var ext = Path.GetExtension(item.FullPath); // includes "."
            var stem = Path.GetFileNameWithoutExtension(item.FullPath);
            var date = item.ModifiedDate;

            string composed = ApplyPattern(PatternBox.Text ?? "", stem, ext, start + i, date);
            if (LowercaseCheck.IsChecked == true) composed = composed.ToLowerInvariant();
            if (ReplaceSpacesCheck.IsChecked == true) composed = composed.Replace(' ', '_');

            // Pattern may or may not include {ext}. If it doesn't end with the original ext, add it.
            if (!composed.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                composed += ext;

            string status;
            string newPath = Path.Combine(dir, composed);

            if (string.IsNullOrWhiteSpace(composed) || composed == ext)
                status = "empty";
            else if (composed.IndexOfAny(invalid) >= 0)
                status = "invalid chars";
            else if (string.Equals(newPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
                status = "unchanged";
            else if (!newNamesSeen.Add(newPath.ToLowerInvariant()))
                status = "duplicate";
            else if (File.Exists(newPath) || Directory.Exists(newPath))
                status = "exists";
            else
                status = "ok";

            rows.Add(new PreviewRow
            {
                Original = Path.GetFileName(item.FullPath),
                NewName = composed,
                Status = status,
                FullPath = item.FullPath,
            });
        }

        PreviewList.ItemsSource = rows;

        var problems = rows.Count(r => r.Status != "ok" && r.Status != "unchanged");
        if (problems > 0)
            ErrorText.Text = $"{problems} file(s) have problems and will be skipped or block the rename.";
    }

    /// <summary>
    /// Substitute supported tokens. Recognizes {name}, {ext}, {n}, {n:000} (any
    /// digit count), {date}, {date:format}.
    /// </summary>
    private static string ApplyPattern(string pattern, string stem, string ext, int counter, DateTime date)
    {
        if (string.IsNullOrEmpty(pattern)) return "";

        var sb = new StringBuilder();
        int i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] == '{')
            {
                int close = pattern.IndexOf('}', i + 1);
                if (close < 0) { sb.Append(pattern[i++]); continue; }
                var token = pattern.Substring(i + 1, close - i - 1);
                sb.Append(ResolveToken(token, stem, ext, counter, date));
                i = close + 1;
            }
            else
            {
                sb.Append(pattern[i++]);
            }
        }
        return sb.ToString();
    }

    private static string ResolveToken(string token, string stem, string ext, int counter, DateTime date)
    {
        var t = token.Trim();
        if (t.Equals("name", StringComparison.OrdinalIgnoreCase)) return stem;
        if (t.Equals("ext", StringComparison.OrdinalIgnoreCase))  return ext;
        if (t.StartsWith("n", StringComparison.OrdinalIgnoreCase) && (t.Length == 1 || t[1] == ':'))
        {
            if (t.Length == 1) return counter.ToString();
            // {n:000}
            var fmt = t.Substring(2);
            if (Regex.IsMatch(fmt, "^0+$"))
                return counter.ToString(new string('0', fmt.Length));
            // unknown format → fall back to plain
            return counter.ToString();
        }
        if (t.Equals("date", StringComparison.OrdinalIgnoreCase)) return date.ToString("yyyy-MM-dd");
        if (t.StartsWith("date:", StringComparison.OrdinalIgnoreCase))
        {
            var fmt = token.Substring("date:".Length);
            try { return date.ToString(fmt); } catch { return date.ToString("yyyy-MM-dd"); }
        }
        // Unknown token → leave the literal so user sees the typo.
        return "{" + token + "}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewList.ItemsSource is not IEnumerable<PreviewRow> rows) { DialogResult = false; return; }
        var rowList = rows.ToList();

        // Block if any non-unchanged row has problems.
        var blocking = rowList.Where(r => r.Status != "ok" && r.Status != "unchanged").ToList();
        if (blocking.Count > 0)
        {
            ErrorText.Text = $"Cannot rename: {blocking.Count} file(s) would conflict, duplicate, or be invalid.";
            return;
        }

        var plan = new List<(string OldPath, string NewPath)>();
        foreach (var r in rowList)
        {
            if (r.Status == "ok")
            {
                var dir = Path.GetDirectoryName(r.FullPath) ?? "";
                plan.Add((r.FullPath, Path.Combine(dir, r.NewName)));
            }
        }
        if (plan.Count == 0) { DialogResult = false; return; }

        Plan = plan;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
