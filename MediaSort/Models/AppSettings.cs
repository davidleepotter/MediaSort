using System.Collections.Generic;

namespace MediaSort.Models;

public enum ViewMode
{
    List,
    Details,
    Thumbnails
}

public enum SortKey
{
    Name,
    Size,
    Aspect,
    Modified,
    Kind,
    Duration,
    // Append new keys at the end — SortKeyCombo binds by enum ordinal.
    Created
}

public enum ThemeOverride
{
    System,
    Light,
    Dark
}

public enum DateFilterMode
{
    All,
    Last7Days,
    Last30Days,
    ThisYear,
    Custom
}

public enum AspectGroup
{
    All,
    Portrait,
    Landscape,
    Square
}

/// <summary>What to do when a destination button is activated for the selected file(s).</summary>
public enum FileAction
{
    Move,
    Copy,
    Delete
}

public class SerializableDestination
{
    public string Name { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string HotKey { get; set; } = "None";
    public string Modifiers { get; set; } = "None";
    /// <summary>If non-empty, only items of this MediaKind name are accepted ("Image" / "Video").</summary>
    public string KindFilter { get; set; } = "";
    /// <summary>Optional subfolder routing template (e.g. "{date:yyyy-MM}").</summary>
    public string SubfolderTemplate { get; set; } = "";
    /// <summary>Optional rename template applied at move time.</summary>
    public string RenameTemplate { get; set; } = "";
    /// <summary>(#17) Per-destination action override. "" = inherit toolbar, otherwise "Move"/"Copy"/"Delete".</summary>
    public string ActionOverride { get; set; } = "";
    /// <summary>(#10) Per-destination tint color (hex like "#FFAA00"). Empty = no tint strip.</summary>
    public string AccentColor { get; set; } = "";
}

public class PerFolderState
{
    public string Path { get; set; } = "";
    public ViewMode ViewMode { get; set; } = ViewMode.Details;
    public SortKey SortKey { get; set; } = SortKey.Name;
    public bool SortDescending { get; set; } = false;
    public string LastSelectedFile { get; set; } = "";
}

public class AppSettings
{
    public string SourceFolder { get; set; } = "";
    public bool RecursiveScan { get; set; } = false;
    public ViewMode ViewMode { get; set; } = ViewMode.Details;
    public SortKey SortKey { get; set; } = SortKey.Name;
    public bool SortDescending { get; set; } = false;
    public List<SerializableDestination> Destinations { get; set; } = new();

    // ---- Filtering ----
    public DateFilterMode DateFilter { get; set; } = DateFilterMode.All;
    public AspectGroup AspectGroupFilter { get; set; } = AspectGroup.All;

    // ---- UI / theming ----
    public ThemeOverride ThemeOverride { get; set; } = ThemeOverride.Dark;
    public string AccentColor { get; set; } = "#2D7BD4"; // hex; empty/invalid -> default
    public int AnimationDurationMs { get; set; } = 420;
    public int ThumbnailSize { get; set; } = 120; // edge length of thumbnail tile in pixels (60–240)

    // ---- Behavior ----
    public ConflictPolicySetting ConflictPolicy { get; set; } = ConflictPolicySetting.Prompt;
    public bool AutoAdvanceAfterMove { get; set; } = true;

    /// <summary>When true, left-clicking a destination button asks for confirmation
    /// before running its action. Bound-key moves and "." repeat are NOT affected.
    /// User can opt out via the dialog's "Don't ask again" checkbox.</summary>
    public bool ConfirmDestinationClick { get; set; } = true;

    /// <summary>What action to perform when sending selected items to a destination.</summary>
    public FileAction Action { get; set; } = FileAction.Move;

    /// <summary>When false (default), files and folders with the Hidden or System attribute are skipped during folder scans.</summary>
    public bool IncludeHiddenFiles { get; set; } = false;

    // ---- Memory ----
    public List<PerFolderState> FolderStates { get; set; } = new();

    /// <summary>Most-recently-used source folders, newest first. Capped at 10 entries.</summary>
    public List<string> RecentSourceFolders { get; set; } = new();

    /// <summary>Full paths of items the user has starred/favorited (#11).</summary>
    public List<string> Favorites { get; set; } = new();

    // ---- Find Duplicates ----
    /// <summary>
    /// Hamming-distance threshold for the dHash perceptual-hash comparison used
    /// by Find Duplicates. Lower = stricter (only nearly-identical photos
    /// match); higher = looser (more matches, more false positives). Valid
    /// range 0–16. Default 4 is the recommended dHash threshold.
    /// </summary>
    public int DuplicateThreshold { get; set; } = 4;

    // ---- (#20) Audio preview for videos ----
    /// <summary>Video player volume 0..100. Persists across sessions.</summary>
    public int VideoVolume { get; set; } = 60;
    /// <summary>When true, the video player is muted regardless of VideoVolume.</summary>
    public bool VideoMuted { get; set; } = false;

    // ---- Layout: persisted splitter sizes ----
    /// <summary>Width in pixels of the left source-list panel. 0 = use default.</summary>
    public double LeftPanelWidth { get; set; } = 0;
    /// <summary>Width in pixels of the right destinations panel. 0 = use default.</summary>
    public double RightPanelWidth { get; set; } = 0;

    // ---- Window placement (size + position + maximized state) ----
    // All four numeric fields default to NaN so a fresh install falls back to the
    // hard-coded XAML size/position. Loading code must check double.IsNaN before
    // applying. Saved values are the *restored* bounds (Window.RestoreBounds) so
    // a maximized window still remembers its un-maximized footprint.
    /// <summary>Saved window left in DIPs. NaN = no saved value, use XAML default.</summary>
    public double WindowLeft { get; set; } = double.NaN;
    /// <summary>Saved window top in DIPs. NaN = no saved value, use XAML default.</summary>
    public double WindowTop { get; set; } = double.NaN;
    /// <summary>Saved window width in DIPs. NaN = no saved value, use XAML default.</summary>
    public double WindowWidth { get; set; } = double.NaN;
    /// <summary>Saved window height in DIPs. NaN = no saved value, use XAML default.</summary>
    public double WindowHeight { get; set; } = double.NaN;
    /// <summary>True if the window was maximized at last close.</summary>
    public bool WindowMaximized { get; set; } = false;

    // ---- Layout: pane order + visibility (Show/Hide + swap-position toggles) ----
    /// <summary>Comma-separated order of the three main panes from left to right.
    /// Tokens: "Source", "Preview", "Destinations". Default = "Source,Preview,Destinations".
    /// Invalid values fall back to the default at load time.</summary>
    public string PaneOrder { get; set; } = "Source,Preview,Destinations";
    /// <summary>If false, the source pane is collapsed (column width = 0, splitter hidden).</summary>
    public bool SourcePaneVisible { get; set; } = true;
    /// <summary>If false, the preview pane is collapsed.</summary>
    public bool PreviewPaneVisible { get; set; } = true;
    /// <summary>If false, the destinations pane is collapsed.</summary>
    public bool DestinationsPaneVisible { get; set; } = true;

    // ---- Destination button text styling ----
    // Each of the four lines on a destination button (folder name, key/hotkey,
    // folder path, file count badge) has its own font family + size so users can
    // tune readability. Empty family => system default (Segoe UI on Windows).
    /// <summary>Font family for the destination's folder/name (top, bold) line.</summary>
    public string DestNameFontFamily { get; set; } = "";
    /// <summary>Font size in pixels for the destination's folder/name line.</summary>
    public double DestNameFontSize { get; set; } = 12;

    /// <summary>Font family for the destination's hotkey/shortcut line.</summary>
    public string DestKeyFontFamily { get; set; } = "";
    /// <summary>Font size in pixels for the destination's hotkey line.</summary>
    public double DestKeyFontSize { get; set; } = 10;

    /// <summary>Font family for the destination's folder-path line.</summary>
    public string DestPathFontFamily { get; set; } = "";
    /// <summary>Font size in pixels for the destination's folder-path line.</summary>
    public double DestPathFontSize { get; set; } = 10;

    /// <summary>Font family for the destination's file-count badge line.</summary>
    public string DestBadgeFontFamily { get; set; } = "";
    /// <summary>Font size in pixels for the destination's file-count badge line.</summary>
    public double DestBadgeFontSize { get; set; } = 10;

    /// <summary>Overall size of destination buttons (Compact / Normal / Large).
    /// Drives MinHeight and inner Padding via DynamicResources.</summary>
    public DestButtonSizeMode DestButtonSize { get; set; } = DestButtonSizeMode.Normal;
}

public enum DestButtonSizeMode
{
    Compact,
    Normal,
    Large
}

public enum ConflictPolicySetting
{
    Prompt,
    AlwaysRename,
    AlwaysOverwrite,
    AlwaysSkip
}
