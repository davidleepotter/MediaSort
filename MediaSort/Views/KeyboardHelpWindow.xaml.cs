using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MediaSort.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;

namespace MediaSort.Views;

public partial class KeyboardHelpWindow : Window
{
    /// <summary>One row in the searchable cheatsheet.</summary>
    public sealed class ShortcutEntry
    {
        public string KeyDisplay { get; init; } = "";
        public string Description { get; init; } = "";
        public string Category { get; init; } = "";
    }

    // Single source of truth for the cheatsheet. Keep this in sync as features ship —
    // it's a flat list so adding/removing a shortcut is one line. Categories are used
    // for grouping headers in the UI; ordering inside a category is preserved.
    /// <summary>Public read-only access for in-process re-use (e.g. by ShortcutOverlay).</summary>
    public static ShortcutEntry[] AllShortcutsPublic => AllShortcuts;

    private static readonly ShortcutEntry[] AllShortcuts =
    {
        // Navigation
        new() { Category = "Navigation", KeyDisplay = "↑ / ↓ / ← / →", Description = "Move selection in the source list" },
        new() { Category = "Navigation", KeyDisplay = "Home / End",     Description = "Jump to first/last item (Source header also has Top / Bottom buttons)" },
        new() { Category = "Navigation", KeyDisplay = "Page Up/Down",   Description = "Page through the list" },
        new() { Category = "Navigation", KeyDisplay = "Ctrl+F",         Description = "Focus the search/quick-filter box. Esc clears." },

        // Sorting / moving
        new() { Category = "Sorting / moving", KeyDisplay = "Destination hotkey", Description = "Move selected item(s) to that destination (no confirm — always instant)" },
        new() { Category = "Sorting / moving", KeyDisplay = "Shift+hotkey…",      Description = "Hold Shift and tap multiple destination hotkeys to queue. Release to send: every queued destination gets a Copy, the LAST one uses the toolbar Action. Esc cancels." },
        new() { Category = "Sorting / moving", KeyDisplay = ".",                  Description = "Repeat last destination (send selection to the last destination you used)" },
        new() { Category = "Sorting / moving", KeyDisplay = "F",                  Description = "Toggle star/favorite on the current selection (persists across sessions)" },
        new() { Category = "Sorting / moving", KeyDisplay = "F2",                 Description = "Rename the selected file. Multi-select opens batch rename." },
        new() { Category = "Sorting / moving", KeyDisplay = "Ctrl+Z",             Description = "Undo last move/copy/delete batch" },
        new() { Category = "Sorting / moving", KeyDisplay = "Ctrl+H",             Description = "Open History — browse all batches and undo any one of them (with Clear History)" },
        new() { Category = "Sorting / moving", KeyDisplay = "N",                  Description = "Skip — advance to next without moving" },
        new() { Category = "Sorting / moving", KeyDisplay = "Delete",             Description = "Send selected item(s) to Recycle Bin" },
        new() { Category = "Sorting / moving", KeyDisplay = "Ctrl+A",             Description = "Select all visible items (toggles to Unselect All)" },

        // Tags / ratings (UX #14)
        new() { Category = "Tags & ratings", KeyDisplay = "Ctrl+T",       Description = "Open the batch tag editor for the current selection (rating + tags)" },
        new() { Category = "Tags & ratings", KeyDisplay = "Tags… button", Description = "Toolbar button — same as Ctrl+T. Set rating Leave/Clear/1–5★ and add/remove/replace tags. Click an existing-tag chip to add it; click ✕ next to a chip to delete that tag from every file in the store." },
        new() { Category = "Tags & ratings", KeyDisplay = "Min ★ combo",  Description = "Toolbar filter — show only items rated at least this many stars (Any / ≥1 / ≥2 / ≥3 / ≥4 / 5)" },
        new() { Category = "Tags & ratings", KeyDisplay = "Tag: textbox", Description = "Toolbar filter — show only items whose tags contain this substring (case-insensitive)" },

        // Pane layout (UX #15)
        new() { Category = "Pane layout", KeyDisplay = "◀ / ▶ in pane header", Description = "Swap this pane (Source / Preview / Destinations) one slot to the left or right" },
        new() { Category = "Pane layout", KeyDisplay = "✕ in pane header",      Description = "Hide this pane (last visible pane is protected). Restore via Panes▾ in toolbar." },
        new() { Category = "Pane layout", KeyDisplay = "Panes ▾ button",        Description = "Toolbar menu — toggle Source/Preview/Destinations visibility, or Reset to default layout" },
        new() { Category = "Pane layout", KeyDisplay = "Drag splitter",           Description = "Drag the 5px gap between two visible panes to resize. Sizes persist per slot across restarts." },

        // View / preview
        new() { Category = "View / preview", KeyDisplay = "Space",       Description = "Play/pause video preview" },
        new() { Category = "View / preview", KeyDisplay = "M",           Description = "Mute / unmute audio" },
        new() { Category = "View / preview", KeyDisplay = "Mouse wheel", Description = "Zoom image preview" },
        new() { Category = "View / preview", KeyDisplay = "Click + drag", Description = "Pan zoomed image" },
        new() { Category = "View / preview", KeyDisplay = "0",           Description = "Reset image zoom to fit" },
        new() { Category = "View / preview", KeyDisplay = "? / F1",      Description = "Show this help" },

        // Mouse on destination buttons
        new() { Category = "Destination buttons (mouse)", KeyDisplay = "Click",      Description = "Send the current selection to that destination. Confirms first by default — disable in Settings or via 'Don't ask again' on the prompt. (Hotkey moves never confirm.)" },
        new() { Category = "Destination buttons (mouse)", KeyDisplay = "Ctrl+Click", Description = "Open the destination folder in File Explorer (no move)" },
        new() { Category = "Destination buttons (mouse)", KeyDisplay = "Right-click", Description = "Open menu: Open folder, Use as source, Move up/down, Edit, Remove" },
        new() { Category = "Destination buttons (mouse)", KeyDisplay = "⋮⋮ handle",  Description = "Click and hold the dotted handle on the left, then drag up or down to reorder" },

        // Right-click menu (source list)
        new() { Category = "Source list right-click", KeyDisplay = "Open in Explorer",     Description = "Reveal the file in File Explorer" },
        new() { Category = "Source list right-click", KeyDisplay = "Copy full path",       Description = "Copy the file path(s) to the clipboard" },
        new() { Category = "Source list right-click", KeyDisplay = "Rename...",            Description = "Rename file (single) or batch-rename multiple selections (also F2)" },
        new() { Category = "Source list right-click", KeyDisplay = "Rotate ▸",             Description = "Rotate selected image(s) 90° CW, 90° CCW, or 180°. Modifies file on disk." },
        new() { Category = "Source list right-click", KeyDisplay = "Send to ▸",            Description = "Submenu listing every destination — pick one to send the selection there" },
        new() { Category = "Source list right-click", KeyDisplay = "Send to Recycle Bin",  Description = "Move selected file(s) to the Windows Recycle Bin (also Delete key)" },

        // Status bar / widgets
        new() { Category = "Status bar widgets", KeyDisplay = "Click 'Session: …'", Description = "Open the session statistics popup with per-action breakdown and Reset" },

        // Other
        new() { Category = "Other", KeyDisplay = "Ctrl+,",         Description = "Open Settings" },
        new() { Category = "Other", KeyDisplay = "F5",             Description = "Refresh source list" },
        new() { Category = "Other", KeyDisplay = "Find Duplicates", Description = "Toolbar button — perceptual-hash scan finds visually similar images. Threshold in Settings." },
        new() { Category = "Other", KeyDisplay = "Drag from Explorer", Description = "Drop files or folders onto the source list to add them" },
        new() { Category = "Other", KeyDisplay = "Per-destination tint", Description = "Edit a destination → Tint color to assign a color strip for visual grouping" },
    };

    private readonly System.Collections.ObjectModel.ObservableCollection<ShortcutEntry> _filtered = new();

    public KeyboardHelpWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);

        // Wire the ItemsControl to a CollectionView grouped by Category. The
        // _filtered ObservableCollection gets repopulated every time the user types,
        // and the grouping picks up automatically.
        var view = CollectionViewSource.GetDefaultView(_filtered);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ShortcutEntry.Category)));
        ShortcutsList.ItemsSource = view;

        ApplyFilter("");
        Loaded += (_, _) => FilterBox.Focus();
    }

    /// <summary>Open the help dialog scrolled to the "Destinations &amp; Flow" section.</summary>
    public void ShowFlowSection()
    {
        if (NavFlow != null) NavFlow.IsChecked = true;
    }

    private void NavShortcuts_Checked(object sender, RoutedEventArgs e)
    {
        if (ShortcutsView == null || FlowView == null) return;
        ShortcutsView.Visibility = Visibility.Visible;
        FlowView.Visibility = Visibility.Collapsed;
    }

    private void NavFlow_Checked(object sender, RoutedEventArgs e)
    {
        if (ShortcutsView == null || FlowView == null) return;
        ShortcutsView.Visibility = Visibility.Collapsed;
        FlowView.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Filter ---------------------------------------------------------

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var t = FilterBox.Text ?? "";
        if (FilterPlaceholder != null)
            FilterPlaceholder.Visibility = string.IsNullOrEmpty(t) ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter(t);
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(FilterBox.Text))
            {
                FilterBox.Text = "";
                e.Handled = true;
            }
        }
    }

    private void ApplyFilter(string query)
    {
        _filtered.Clear();
        IEnumerable<ShortcutEntry> src = AllShortcuts;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            src = AllShortcuts.Where(s =>
                s.KeyDisplay.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        int count = 0;
        foreach (var entry in src) { _filtered.Add(entry); count++; }

        if (FilterCountText != null)
        {
            FilterCountText.Text = string.IsNullOrWhiteSpace(query)
                ? $"{count} shortcuts"
                : $"{count} of {AllShortcuts.Length} shortcuts match \u201C{query}\u201D";
        }
    }

    // ---- Markdown export ------------------------------------------------

    private void CopyMarkdown_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            // Group the currently-visible (filtered) entries by category in display order.
            var grouped = _filtered.GroupBy(s => s.Category);
            sb.AppendLine("# MediaSort shortcuts");
            sb.AppendLine();
            foreach (var g in grouped)
            {
                sb.Append("## ").AppendLine(g.Key);
                sb.AppendLine();
                sb.AppendLine("| Key | Action |");
                sb.AppendLine("| --- | ------ |");
                foreach (var s in g)
                {
                    var key = s.KeyDisplay.Replace("|", "\\|");
                    var desc = s.Description.Replace("|", "\\|");
                    sb.Append("| ").Append(key).Append(" | ").Append(desc).AppendLine(" |");
                }
                sb.AppendLine();
            }
            System.Windows.Clipboard.SetText(sb.ToString());
            FilterCountText.Text = $"Copied {_filtered.Count} shortcuts to clipboard as Markdown.";
        }
        catch (Exception ex)
        {
            FilterCountText.Text = "Copy failed: " + ex.Message;
        }
    }
}
