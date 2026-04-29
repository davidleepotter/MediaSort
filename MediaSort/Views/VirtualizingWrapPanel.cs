using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
// Per project policy: fully qualify types that collide with WinForms / GDI+.
// Size and Point both exist in System.Drawing AND System.Windows; alias the WPF
// versions so we never accidentally pick up the Drawing variants.
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using System.Windows.Media;

namespace MediaSort.Views;

/// <summary>
/// A wrap panel that supports UI virtualization. WPF's stock WrapPanel does NOT
/// virtualize: it instantiates every container for every bound item, so a 1,938-item
/// thumbnail grid pays measure/arrange cost for every tile every time any item's
/// PropertyChanged fires (e.g. when a thumbnail finishes decoding and is assigned
/// to its MediaItem). On a network share scan that meant thousands of layout passes
/// during probe \u2014 mouse felt sluggish even at 6% CPU.
///
/// This panel only realizes containers for items that are visible (or near-visible)
/// in the scroll viewport, and recycles them as the user scrolls. Implementation
/// follows the standard IScrollInfo + ItemContainerGenerator pattern.
///
/// Item size is taken from the FIRST measured child \u2014 we assume tiles are uniform
/// (which they are: the source list ItemTemplate uses fixed Width/Height bindings
/// to ThumbTileSize/ThumbTileHeight resources). If any child measures larger we
/// expand the cell to fit.
/// </summary>
public class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    private Size _itemSize = new(120, 120);   // updated from first measured child
    private int _itemsPerRow = 1;             // recomputed in MeasureOverride
    private Size _extent;
    private Size _viewport;
    private Point _offset;

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    /// <summary>How much of an item's height equals one mouse-wheel "click".</summary>
    private double LineSize => Math.Max(1, _itemSize.Height);

    public void LineDown() => SetVerticalOffset(_offset.Y + LineSize);
    public void LineUp() => SetVerticalOffset(_offset.Y - LineSize);
    public void LineLeft() => SetHorizontalOffset(_offset.X - LineSize);
    public void LineRight() => SetHorizontalOffset(_offset.X + LineSize);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + 3 * LineSize);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - 3 * LineSize);
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - 3 * LineSize);
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + 3 * LineSize);

    public void SetHorizontalOffset(double offset)
    {
        var clamped = Math.Max(0, Math.Min(offset, _extent.Width - _viewport.Width));
        if (Math.Abs(clamped - _offset.X) < 0.5) return;
        _offset.X = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void SetVerticalOffset(double offset)
    {
        var clamped = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (Math.Abs(clamped - _offset.Y) < 0.5) return;
        _offset.Y = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        // Find which item index this visual belongs to and scroll it into view.
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            if (InternalChildren[i] == visual)
            {
                // We don't know the actual item index from here without the generator,
                // but the child has already been positioned, so its offset relative
                // to us is meaningful.
                var childTop = InternalChildren[i].DesiredSize.Height; // unused; fall through to bring-into-view via owner
                if (ScrollOwner != null)
                {
                    var transform = visual.TransformToAncestor(this);
                    var childRect = transform.TransformBounds(rectangle);
                    if (childRect.Top < 0) SetVerticalOffset(_offset.Y + childRect.Top);
                    else if (childRect.Bottom > _viewport.Height) SetVerticalOffset(_offset.Y + (childRect.Bottom - _viewport.Height));
                }
                return rectangle;
            }
        }
        return rectangle;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var itemsControl = ItemsControl.GetItemsOwner(this);
        if (itemsControl == null) return new Size(0, 0);

        int itemCount = itemsControl.HasItems ? itemsControl.Items.Count : 0;
        if (itemCount == 0)
        {
            UpdateScrollInfo(availableSize, new Size(0, 0));
            CleanUpItems(0, -1);
            return new Size(0, 0);
        }

        // Use a generous initial cell size if we haven't measured anything yet.
        // Items per row is derived from the available width and the cached item size.
        double availW = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
            ? _itemSize.Width
            : availableSize.Width;
        _itemsPerRow = Math.Max(1, (int)Math.Floor(availW / Math.Max(1, _itemSize.Width)));

        int totalRows = (int)Math.Ceiling((double)itemCount / _itemsPerRow);
        var extent = new Size(_itemsPerRow * _itemSize.Width, totalRows * _itemSize.Height);
        UpdateScrollInfo(availableSize, extent);

        // Compute the visible item-index range based on vertical offset.
        int firstVisibleRow = (int)Math.Floor(_offset.Y / _itemSize.Height);
        int lastVisibleRow  = (int)Math.Ceiling((_offset.Y + availableSize.Height) / _itemSize.Height);
        int firstIndex = Math.Max(0, firstVisibleRow * _itemsPerRow);
        int lastIndex  = Math.Min(itemCount - 1, (lastVisibleRow + 1) * _itemsPerRow - 1);

        // Realize containers for [firstIndex..lastIndex], recycle the rest.
        var generator = ItemContainerGenerator;
        var startPos = generator.GeneratorPositionFromIndex(firstIndex);
        int childIndex = (startPos.Offset == 0) ? startPos.Index : startPos.Index + 1;

        using (generator.StartAt(startPos, GeneratorDirection.Forward, true))
        {
            for (int itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out bool newlyRealized);
                if (newlyRealized)
                {
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child);
                    else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                else if (childIndex < InternalChildren.Count && !ReferenceEquals(InternalChildren[childIndex], child))
                {
                    // Recycled container moved; re-insert at the right index.
                    var existingIndex = InternalChildren.IndexOf(child);
                    if (existingIndex >= 0) RemoveInternalChildRange(existingIndex, 1);
                    InsertInternalChild(childIndex, child);
                }

                child.Measure(new Size(_itemSize.Width, _itemSize.Height));

                // Update item size from the first realized child each pass so a theme
                // change or a different ThumbTileSize resource takes effect promptly.
                if (itemIndex == firstIndex && child.DesiredSize.Width > 0 && child.DesiredSize.Height > 0)
                {
                    if (child.DesiredSize.Width > _itemSize.Width || child.DesiredSize.Height > _itemSize.Height
                        || _itemSize.Width <= 1 || _itemSize.Height <= 1)
                    {
                        _itemSize = child.DesiredSize;
                    }
                }
            }
        }

        CleanUpItems(firstIndex, lastIndex);
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemsControl = ItemsControl.GetItemsOwner(this);
        if (itemsControl == null) return finalSize;

        int itemCount = itemsControl.HasItems ? itemsControl.Items.Count : 0;
        if (itemCount == 0) return finalSize;

        // Recompute itemsPerRow from finalSize so wrap matches what the user sees.
        _itemsPerRow = Math.Max(1, (int)Math.Floor(finalSize.Width / Math.Max(1, _itemSize.Width)));

        int firstVisibleRow = (int)Math.Floor(_offset.Y / _itemSize.Height);
        int firstIndex = Math.Max(0, firstVisibleRow * _itemsPerRow);

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            int itemIndex = firstIndex + i;
            int row = itemIndex / _itemsPerRow;
            int col = itemIndex % _itemsPerRow;
            double x = col * _itemSize.Width;
            double y = row * _itemSize.Height - _offset.Y;
            child.Arrange(new Rect(x, y, _itemSize.Width, _itemSize.Height));
        }

        return finalSize;
    }

    private void UpdateScrollInfo(Size availableSize, Size extent)
    {
        bool changed = false;
        if (extent != _extent) { _extent = extent; changed = true; }
        var viewport = availableSize;
        if (double.IsInfinity(viewport.Width)) viewport.Width = extent.Width;
        if (double.IsInfinity(viewport.Height)) viewport.Height = extent.Height;
        if (viewport != _viewport) { _viewport = viewport; changed = true; }
        // Clamp offset to new extent.
        var clampedY = Math.Max(0, Math.Min(_offset.Y, extent.Height - viewport.Height));
        var clampedX = Math.Max(0, Math.Min(_offset.X, extent.Width - viewport.Width));
        if (Math.Abs(clampedY - _offset.Y) > 0.5 || Math.Abs(clampedX - _offset.X) > 0.5)
        {
            _offset.X = clampedX; _offset.Y = clampedY; changed = true;
        }
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    /// <summary>Recycle / remove containers outside [firstVisible..lastVisible].</summary>
    private void CleanUpItems(int firstVisible, int lastVisible)
    {
        // The non-generic IItemContainerGenerator does not expose Recycle; the
        // recycling-aware variant (IRecyclingItemContainerGenerator) does. The host
        // ListBox sets VirtualizationMode=Recycling, so the actual generator instance
        // implements it. Cast and call Recycle so containers are reused instead of
        // torn down.
        var generator = (IRecyclingItemContainerGenerator)ItemContainerGenerator;
        for (int i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var pos = new GeneratorPosition(i, 0);
            int itemIndex = ItemContainerGenerator.IndexFromGeneratorPosition(pos);
            if (itemIndex < firstVisible || itemIndex > lastVisible)
            {
                generator.Recycle(pos, 1);
                RemoveInternalChildRange(i, 1);
            }
        }
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Reset:
                // Full reset: recycle everything; next measure pass realizes from scratch.
                _offset = new Point(0, 0);
                ScrollOwner?.InvalidateScrollInfo();
                InvalidateMeasure();
                break;
        }
        base.OnItemsChanged(sender, args);
    }
}
