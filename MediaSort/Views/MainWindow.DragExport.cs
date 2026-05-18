// MainWindow.DragExport.cs
//
// Outgoing drag-and-drop: the user grabs a media item (in any of the three
// list views — List, Details, Thumbs) or the preview image and drags it out
// of MediaSort into Explorer, Photoshop, a chat client, a web-upload widget,
// etc. The drop target receives a standard Windows FileDrop containing the
// full path(s) of the dragged item(s).
//
// Design notes:
//
// 1. We only carry CopyEffect. WPF's DoDragDrop will let the target pick from
//    the allowed-effects mask, and Explorer's default for a drop on a folder
//    on a different drive is Copy anyway, but if the target picks "Move",
//    Explorer would delete the original — that's destructive behavior the
//    user didn't ask for. Always Copy.
//
// 2. We have to coexist with click-to-select and multi-select rubber-banding
//    in the ListBox/ListView. The pattern is:
//        - PreviewMouseLeftButtonDown stores the press location AND captures
//          which MediaItem is under the mouse (if any).
//        - PreviewMouseMove ignores anything below MinimumHorizontalDragDistance.
//          Only past that threshold do we kick off DoDragDrop.
//        - We do NOT Handle the button-down event, so the underlying ListBox
//          still gets it and updates selection normally.
//
// 3. We carry every currently-selected item, not just the one under the
//    cursor. The user expects "I selected 12, I drag one, all 12 go." This
//    matches Explorer behavior. Edge case: if the user grabs an unselected
//    item, we drag just that one (also Explorer behavior).
//
// 4. The incoming-drop handlers (MediaList_Drop, etc.) reject FileDrop from
//    ourselves by virtue of the source-path-already-in-list dedup, so even
//    if someone drags out and back in nothing weird happens.
//
// 5. Toggled off via AppSettings.EnableExportDragDrop. Users with twitchy
//    touchpads can disable.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MediaSort.Models;
using MediaSort.Services;
// Disambiguate WPF vs WinForms types. The main project has UseWindowsForms=true
// for the legacy folder-browser dialog, which drags System.Windows.Forms into
// scope. Without these aliases MouseEventArgs and Point are ambiguous.
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Point = System.Windows.Point;
using Cursor = System.Windows.Input.Cursor;
using Cursors = System.Windows.Input.Cursors;
using CheckBox = System.Windows.Controls.CheckBox;
using DataObject = System.Windows.DataObject;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;

namespace MediaSort.Views;

public partial class MainWindow
{
    // Press-point + originating element captured on MouseLeftButtonDown so
    // MouseMove can decide whether the cursor has moved far enough to count
    // as a drag rather than a click.
    private Point? _exportDragStartPoint;
    private DependencyObject? _exportDragSource;
    private bool _exportDragInFlight; // re-entrancy guard for DoDragDrop

    /// <summary>
    /// Wired on ListView_List / ListView_Details / ListView_Thumbs in XAML.
    /// Just records the press location; selection logic runs on the bubbling
    /// event as usual.
    /// </summary>
    private void MediaList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_settings.EnableExportDragDrop) { _exportDragStartPoint = null; return; }
        if (_exportDragInFlight) { _exportDragStartPoint = null; return; }

        // Don't start a drag if the user pressed on a CheckBox (those are
        // the per-row selection toggles). Without this, every checkbox click
        // tracks the press as a potential drag origin which is fine
        // functionally but wastes work.
        if (e.OriginalSource is DependencyObject d && IsInsideCheckBox(d))
        {
            _exportDragStartPoint = null;
            return;
        }

        // Don't try to drag from header/scrollbar territory.
        if (e.OriginalSource is DependencyObject d2 && IsInsideHeaderOrScrollbar(d2))
        {
            _exportDragStartPoint = null;
            return;
        }

        if (sender is not IInputElement el) return;
        _exportDragStartPoint = e.GetPosition(el);
        _exportDragSource = e.OriginalSource as DependencyObject;
        // Intentionally NOT setting e.Handled — let the ListBox handle
        // selection normally on the bubbling pass.
    }

    /// <summary>
    /// Wired on ListView_List / ListView_Details / ListView_Thumbs in XAML.
    /// Initiates DoDragDrop once the cursor has moved past the system drag
    /// threshold while the left button is still down.
    /// </summary>
    private void MediaList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_settings.EnableExportDragDrop) return;
        if (_exportDragInFlight) return;
        if (_exportDragStartPoint is not Point start) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _exportDragStartPoint = null; return; }

        if (sender is not IInputElement el) return;
        var pos = e.GetPosition(el);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // Past threshold — figure out which row (if any) the press landed on.
        if (sender is not ItemsControl items) return;
        var pressed = FindAncestor<FrameworkElement>(_exportDragSource) is FrameworkElement fe
            ? fe.DataContext as MediaItem
            : null;

        var paths = BuildDragPaths(items, pressed);
        if (paths.Count == 0)
        {
            _exportDragStartPoint = null;
            return;
        }

        BeginFileDragExport(items, paths);
    }

    /// <summary>
    /// Wired on the PreviewImage (and we mirror the start/threshold pattern
    /// here too — the preview image already has its own MouseLeftButtonDown
    /// for pan/zoom, so we hook MouseRightButtonDown? No — pan/zoom only
    /// engages on a drag inside the image. We piggyback the existing
    /// MouseDown handler indirectly by exposing this entry point from XAML's
    /// PreviewMouseLeftButtonDown which fires first.
    /// </summary>
    private void PreviewImage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_settings.EnableExportDragDrop) { _exportDragStartPoint = null; return; }
        if (_exportDragInFlight) return;
        if (sender is not IInputElement el) return;
        _exportDragStartPoint = e.GetPosition(el);
        _exportDragSource = sender as DependencyObject;
        // Do NOT set Handled — PreviewImage_MouseDown still needs to run
        // for pan-zoom semantics. The threshold check in PreviewMouseMove
        // means a tiny mouse wobble is treated as a click/pan-start, not
        // a drag-export.
    }

    private void PreviewImage_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_settings.EnableExportDragDrop) return;
        if (_exportDragInFlight) return;
        if (_exportDragStartPoint is not Point start) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _exportDragStartPoint = null; return; }

        if (sender is not IInputElement el) return;
        var pos = e.GetPosition(el);
        if (Math.Abs(pos.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        // Preview always represents the current selection. Use whatever
        // _currentItem points to, fall back to the active list selection.
        var paths = BuildDragPathsFromSelection();
        if (paths.Count == 0) return;

        // Cancel any pan that the bubble-pass PreviewImage_MouseDown started
        // for this same click. DoDragDrop is modal and will steal mouse
        // capture; without this, after the drag returns _imagePanning is
        // still true and the next non-drag MouseMove erroneously scrolls.
        _imagePanning = false;
        try { PreviewImage.ReleaseMouseCapture(); } catch { }
        Cursor = Cursors.Arrow;

        BeginFileDragExport(sender as DependencyObject, paths);
    }

    /// <summary>
    /// Compose the list of file paths to carry in the drag. Rule:
    /// - If the user pressed on a row that is part of the current multi-
    ///   selection, drag the entire selection.
    /// - Otherwise drag just the pressed row.
    /// - Filter out files that no longer exist on disk (network outage,
    ///   external deletion); silently drop them so we never advertise a
    ///   ghost path to Explorer.
    /// </summary>
    private static List<string> BuildDragPaths(ItemsControl items, MediaItem? pressed)
    {
        var selected = items.Items
            .OfType<MediaItem>()
            .Where(m => m.IsSelected)
            .ToList();

        IEnumerable<MediaItem> source;
        if (pressed != null && selected.Contains(pressed))
            source = selected;
        else if (pressed != null)
            source = new[] { pressed };
        else
            source = selected;

        return source
            .Select(m => m.FullPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// For the preview pane: just use the active list's selection, falling
    /// back to whichever list view is visible if the user clicked into the
    /// preview directly without touching the list.
    /// </summary>
    private List<string> BuildDragPathsFromSelection()
    {
        // Pick the visible list. We don't bother tracking _currentItem
        // explicitly here — the visible ItemsControl's SelectedItems is
        // the source of truth.
        ItemsControl? active = null;
        if (ListView_Thumbs.Visibility == Visibility.Visible) active = ListView_Thumbs;
        else if (ListView_Details.Visibility == Visibility.Visible) active = ListView_Details;
        else if (ListView_List.Visibility == Visibility.Visible) active = ListView_List;

        if (active == null) return new List<string>();
        return BuildDragPaths(active, pressed: null);
    }

    /// <summary>
    /// Kick off the actual DoDragDrop with FileDrop data. We guard with
    /// _exportDragInFlight because DoDragDrop is modal — it pumps messages
    /// internally and we don't want our MouseMove to re-enter.
    /// </summary>
    private void BeginFileDragExport(DependencyObject? source, List<string> paths)
    {
        if (paths.Count == 0) return;
        if (source is not DependencyObject) return;

        _exportDragInFlight = true;
        _exportDragStartPoint = null;

        try
        {
            // Tag the data as coming from us so the incoming-drop handler
            // can ignore it and skip the "add to list" pass. The standard
            // FileDrop key is what Explorer/Photoshop/etc consume.
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths.ToArray());
            data.SetData("MediaSort.InternalDrag", true);

            // Show "count" status while drag is in flight. Restored on return.
            var prev = StatusText.Text;
            StatusText.Text = paths.Count == 1
                ? $"Drag: {Path.GetFileName(paths[0])}"
                : $"Drag: {paths.Count} files";

            try
            {
                DragDrop.DoDragDrop(source, data, DragDropEffects.Copy);
            }
            finally
            {
                StatusText.Text = prev;
            }
        }
        catch (Exception ex)
        {
            // A drag failure (rare — usually OLE COM hiccup) should never
            // crash MediaSort. Log and move on.
            try { CrashLogger.Info($"drag-export failed: {ex.Message}"); } catch { }
        }
        finally
        {
            _exportDragInFlight = false;
            _exportDragStartPoint = null;
            _exportDragSource = null;
        }
    }

    private static bool IsInsideCheckBox(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is CheckBox) return true;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static bool IsInsideHeaderOrScrollbar(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is GridViewColumnHeader || d is ScrollBar || d is Thumb) return true;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
