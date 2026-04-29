using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaSort.Models;

namespace MediaSort.Views;

/// <summary>
/// Modal review-and-resolve dialog for the Find Duplicates flow. Hosts a
/// scrollable list of <see cref="DuplicateGroup"/>s; each group is a horizontal
/// strip of thumbnails. Click a thumbnail to mark it as the "keep" winner; the
/// other tiles in the group get the "will be moved" ribbon. Apply moves all
/// non-kept items into the chosen destination using MainWindow.MoveItemsTo so
/// conflict prompts, animations, undo, and destination flash all work the same
/// way as a normal move.
///
/// The dialog never deletes anything itself — it returns the user's choices to
/// MainWindow which performs the move on close.
/// </summary>
public partial class DuplicatesDialog : Window
{
    private static readonly System.Windows.Media.Brush KeepBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly System.Windows.Media.Brush MoveBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x88, 0x3E));

    /// <summary>
    /// True when the user clicked Apply. MainWindow reads <see cref="ChosenDestination"/>
    /// and <see cref="ItemsToMove"/> on success.
    /// </summary>
    public bool ApplyRequested { get; private set; }

    /// <summary>The destination the user picked from the dropdown (may be null).</summary>
    public DestinationButton? ChosenDestination { get; private set; }

    /// <summary>Flat list of items the user wants to move (the non-kept members of every group).</summary>
    public List<MediaItem> ItemsToMove { get; private set; } = new();

    private readonly ObservableCollection<DuplicateGroup> _groups;

    public DuplicatesDialog(IEnumerable<DuplicateGroup> groups, IEnumerable<DestinationButton> destinations)
    {
        InitializeComponent();

        _groups = new ObservableCollection<DuplicateGroup>(groups);
        GroupsList.ItemsSource = _groups;

        // Wire up destinations. We accept all destinations regardless of KindFilter
        // because the move pipeline already filters per-destination, and a duplicate
        // set may include images that match an Image-only destination.
        DestCombo.ItemsSource = destinations.ToList();
        if (DestCombo.Items.Count > 0) DestCombo.SelectedIndex = 0;

        // Refresh ribbons whenever a group's Keep changes so the green/orange
        // strip moves to the new winner without us re-templating the list.
        foreach (var g in _groups)
            g.PropertyChanged += Group_PropertyChanged;

        UpdateSummary();

        // Defer ribbon paint until the ItemsControl has realised its containers.
        Loaded += (_, __) => RefreshAllRibbons();
    }

    private void UpdateSummary()
    {
        int totalFiles = _groups.Sum(g => g.Members.Count);
        int totalMoves = _groups.Sum(g => g.OthersCount);
        SummaryText.Text = $"{_groups.Count} duplicate group(s) · {totalFiles} files · {totalMoves} will be moved";
        ApplyButton.IsEnabled = totalMoves > 0 && _groups.Count > 0;
    }

    private void Group_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DuplicateGroup.Keep))
        {
            RefreshAllRibbons();
            UpdateSummary();
        }
    }

    /// <summary>
    /// Walk every realized thumbnail Button and paint its ribbon based on whether
    /// the bound MediaItem is the owning group's current Keep. Cheap because the
    /// number of duplicate tiles is tiny compared to the source list.
    /// </summary>
    private void RefreshAllRibbons()
    {
        for (int i = 0; i < _groups.Count; i++)
        {
            if (GroupsList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement groupContainer)
                continue;
            // Find the inner ItemsControl that hosts the member tiles.
            var inner = FindDescendant<ItemsControl>(groupContainer, ic => ic.ItemsSource == _groups[i].Members);
            if (inner == null) continue;
            for (int j = 0; j < _groups[i].Members.Count; j++)
            {
                if (inner.ItemContainerGenerator.ContainerFromIndex(j) is not FrameworkElement memberContainer)
                    continue;
                var member = _groups[i].Members[j];
                bool isKeep = ReferenceEquals(member, _groups[i].Keep);
                ApplyRibbon(memberContainer, isKeep);
            }
        }
    }

    private static void ApplyRibbon(DependencyObject memberContainer, bool isKeep)
    {
        var ribbon = FindDescendant<Border>(memberContainer, b => b.Name == "StatusRibbon");
        var badge = FindDescendant<TextBlock>(memberContainer, t => t.Name == "StatusBadge");
        var frame = FindDescendant<Border>(memberContainer, b => b.Name == "Frame");
        if (ribbon == null || badge == null || frame == null) return;

        ribbon.Visibility = Visibility.Visible;
        if (isKeep)
        {
            ribbon.Background = KeepBrush;
            badge.Text = "KEEP";
            frame.BorderBrush = KeepBrush;
        }
        else
        {
            ribbon.Background = MoveBrush;
            badge.Text = "WILL MOVE";
            frame.BorderBrush = MoveBrush;
        }
    }

    private void ThumbButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: MediaItem clicked }) return;
        // Find which group owns this item and mark it as Keep.
        foreach (var g in _groups)
        {
            if (g.Members.Contains(clicked))
            {
                g.Keep = clicked;
                break;
            }
        }
    }

    private void KeepLargest_Click(object sender, RoutedEventArgs e)
    {
        // Reset every group's Keep to the largest-pixel-dimensions winner.
        foreach (var g in _groups)
        {
            var largest = g.Members
                .OrderByDescending(m => (long)m.PixelWidth * m.PixelHeight)
                .ThenByDescending(m => m.SizeBytes)
                .ThenByDescending(m => m.ModifiedDate)
                .FirstOrDefault();
            g.Keep = largest;
        }
        // Refresh in case nothing changed and PropertyChanged didn't fire.
        RefreshAllRibbons();
        UpdateSummary();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (DestCombo.SelectedItem is not DestinationButton dest)
        {
            StatusText.Text = "Pick a destination first.";
            return;
        }
        ChosenDestination = dest;
        ItemsToMove = _groups.SelectMany(g => g.Others).ToList();
        ApplyRequested = true;
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ApplyRequested = false;
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Generic visual-tree descendant search with optional predicate. Returns the
    /// first matching descendant (depth-first) or null.
    /// </summary>
    private static T? FindDescendant<T>(DependencyObject root, System.Func<T, bool>? match = null)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && (match == null || match(typed))) return typed;
            var deeper = FindDescendant<T>(child, match);
            if (deeper != null) return deeper;
        }
        return null;
    }
}
