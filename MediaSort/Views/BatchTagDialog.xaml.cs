using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MediaSort.Models;
using MediaSort.Services;

namespace MediaSort.Views;

/// <summary>
/// Batch tag + rating editor (UX #14). Operates on a snapshot of the
/// selected MediaItems. Persists via TagStore so changes survive restart.
/// </summary>
public partial class BatchTagDialog : Window
{
    private readonly List<MediaItem> _items;
    private readonly TagStore _store;

    public BatchTagDialog(List<MediaItem> items, TagStore store)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
        _items = items ?? new List<MediaItem>();
        _store = store;

        HeaderSubtitle.Text = _items.Count == 1
            ? $"Editing 1 item: {_items[0].FileName}"
            : $"Editing {_items.Count} item(s).";

        // Pre-populate the existing-tags chip list with the union of tags
        // already on the selection (most useful) followed by other tags from
        // the store the user might want to reuse.
        RefreshExistingTagsList();

        // If the selection has a uniform existing rating, hint at it via the
        // matching radio so the user can keep it without clicking "Leave".
        var distinctRatings = _items.Select(i => i.Rating).Distinct().ToList();
        if (distinctRatings.Count == 1 && distinctRatings[0] > 0)
        {
            // Don't auto-flip away from "Leave unchanged" — but DO show the
            // current rating in the subtitle for context.
            HeaderSubtitle.Text += $"  Current rating: {new string('\u2605', distinctRatings[0])}";
        }
        else if (distinctRatings.Count > 1)
        {
            HeaderSubtitle.Text += "  Current rating: mixed";
        }
    }

    private int? GetSelectedRating()
    {
        if (RatingLeaveRadio.IsChecked == true) return null;
        if (RatingClearRadio.IsChecked == true) return 0;
        if (Rating1.IsChecked == true) return 1;
        if (Rating2.IsChecked == true) return 2;
        if (Rating3.IsChecked == true) return 3;
        if (Rating4.IsChecked == true) return 4;
        if (Rating5.IsChecked == true) return 5;
        return null;
    }

    private List<string> ParseTags()
    {
        var raw = TagsInput.Text ?? "";
        return raw
            .Split(new[] { ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Rebuild the chip list from the current selection + store. Called
    /// at construction and after a global tag delete.</summary>
    private void RefreshExistingTagsList()
    {
        var fromSelection = _items
            .SelectMany(i => i.Tags ?? new List<string>())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fromStore = _store.AllTags()
            .Where(t => !fromSelection.Contains(t, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var combined = new List<string>(fromSelection);
        combined.AddRange(fromStore);
        ExistingTagsList.ItemsSource = combined;
    }

    private void ExistingTag_Click(object sender, RoutedEventArgs e)
    {
        // Prefer Tag (set in XAML) so this is robust to chip layout changes.
        var tag = (sender as System.Windows.Controls.Button)?.Tag as string
               ?? ((sender as System.Windows.Controls.Button)?.Content as string);
        if (string.IsNullOrWhiteSpace(tag)) return;

        var existing = ParseTags();
        if (existing.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
            return;
        TagsInput.Text = string.IsNullOrWhiteSpace(TagsInput.Text)
            ? tag
            : TagsInput.Text.TrimEnd(' ', ',') + ", " + tag;
        TagsInput.CaretIndex = TagsInput.Text.Length;
    }

    /// <summary>Permanently remove this tag from EVERY file in the tag store
    /// (not just the current selection). Confirms first; the change is
    /// persisted immediately and live MediaItems are kept in sync so any
    /// active filter / list view refreshes.</summary>
    private void DeleteTag_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as System.Windows.Controls.Button)?.Tag as string;
        if (string.IsNullOrWhiteSpace(tag)) return;

        var (ok, _) = ConfirmDialog.ShowWithSuppress(
            owner: this,
            heading: $"Delete tag \u201c{tag}\u201d?",
            message: $"This removes the tag \u201c{tag}\u201d from every file in the tag store, not just the {_items.Count} item(s) currently selected. This cannot be undone.",
            okText: "Delete",
            cancelText: "Cancel");
        if (!ok) return;

        // Sync the in-memory list on MainWindow so any visible chips/filters
        // re-render. Falls back to just the dialog's selection if the owner
        // isn't a MainWindow (e.g., during tests).
        IEnumerable<MediaItem> live = _items;
        if (Owner is MainWindow mw)
            live = mw.AllItemsRef;

        int touched = _store.DeleteTagGlobally(tag, live);
        _store.SaveIfDirty();
        RefreshExistingTagsList();

        // Also drop from the input box if present.
        if (!string.IsNullOrWhiteSpace(TagsInput.Text))
        {
            var kept = ParseTags()
                .Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                .ToList();
            TagsInput.Text = string.Join(", ", kept);
        }

        // Mark dialog dirty so caller re-runs ApplyFilter even if the user cancels
        // the rest of the dialog — the global delete already persisted.
        _globalChangesMade = true;
    }

    /// <summary>True if a global tag delete happened; even on Cancel the parent
    /// should re-evaluate filters because the change has already persisted.</summary>
    private bool _globalChangesMade;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        int? rating = GetSelectedRating();
        var tags = ParseTags();

        // Tag-mode dispatch.
        bool tagAdd = TagAddRadio.IsChecked == true;
        bool tagRemove = TagRemoveRadio.IsChecked == true;
        bool tagReplace = TagReplaceRadio.IsChecked == true;
        // Leave is the no-op case.

        foreach (var item in _items)
        {
            if (rating.HasValue) _store.SetRating(item, rating.Value);

            if (tags.Count == 0 && !tagReplace)
            {
                // Nothing to add/remove; replace-with-empty is allowed and
                // intentionally clears tags.
                continue;
            }

            if (tagAdd) _store.AddTags(item, tags);
            else if (tagRemove) _store.RemoveTags(item, tags);
            else if (tagReplace) _store.SetTags(item, tags);
        }

        _store.SaveIfDirty();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // If the user only used "Delete tag" then clicked Cancel, return true
        // anyway so MainWindow re-applies filters.
        DialogResult = _globalChangesMade ? true : false;
        Close();
    }
}
