using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace MediaSort.Views;

/// <summary>
/// In-place keyboard help overlay — translucent backdrop + centered card.
/// Toggled by ? / F1 from MainWindow. Esc or click-outside-card closes it.
/// Reuses the shortcut list from <see cref="KeyboardHelpWindow"/> as the single
/// source of truth so both surfaces stay in sync.
/// </summary>
public partial class ShortcutOverlay : System.Windows.Controls.UserControl
{
    private readonly System.Collections.ObjectModel.ObservableCollection<KeyboardHelpWindow.ShortcutEntry> _filtered = new();

    public ShortcutOverlay()
    {
        InitializeComponent();

        var view = CollectionViewSource.GetDefaultView(_filtered);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(KeyboardHelpWindow.ShortcutEntry.Category)));
        ShortcutsList.ItemsSource = view;

        ApplyFilter("");
    }

    /// <summary>Toggle visibility. When showing, focuses the filter box.</summary>
    public void Toggle()
    {
        if (Visibility == Visibility.Visible)
            HideOverlay();
        else
            ShowOverlay();
    }

    public void ShowOverlay()
    {
        Visibility = Visibility.Visible;
    }

    public void HideOverlay()
    {
        Visibility = Visibility.Collapsed;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Visibility == Visibility.Visible)
        {
            // When freshly shown, focus the filter box and reset the filter so the
            // user always opens to a familiar empty state.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                FilterBox.Text = "";
                FilterBox.Focus();
                Keyboard.Focus(FilterBox);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // ---- Backdrop / card click handling ---------------------------------

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Click on the dimmed backdrop (anywhere outside the card) closes.
        HideOverlay();
        e.Handled = true;
    }

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Swallow clicks inside the card so they don't reach the backdrop.
        e.Handled = true;
    }

    // ---- Section nav ----------------------------------------------------

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

    private void Close_Click(object sender, RoutedEventArgs e) => HideOverlay();

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
            // First Esc clears the filter if non-empty; otherwise close the overlay.
            if (!string.IsNullOrEmpty(FilterBox.Text))
            {
                FilterBox.Text = "";
                e.Handled = true;
            }
            else
            {
                HideOverlay();
                e.Handled = true;
            }
        }
    }

    private void ApplyFilter(string query)
    {
        _filtered.Clear();
        IEnumerable<KeyboardHelpWindow.ShortcutEntry> src = KeyboardHelpWindow.AllShortcutsPublic;
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            src = src.Where(s =>
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
                : $"{count} of {KeyboardHelpWindow.AllShortcutsPublic.Length} shortcuts match \u201C{query}\u201D";
        }
    }

    // ---- Markdown export ------------------------------------------------

    private void CopyMarkdown_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
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
