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
    Duration
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
    public ThemeOverride ThemeOverride { get; set; } = ThemeOverride.System;
    public string AccentColor { get; set; } = "#2D7BD4"; // hex; empty/invalid -> default
    public int AnimationDurationMs { get; set; } = 420;

    // ---- Behavior ----
    public ConflictPolicySetting ConflictPolicy { get; set; } = ConflictPolicySetting.Prompt;
    public bool AutoAdvanceAfterMove { get; set; } = true;

    // ---- Memory ----
    public List<PerFolderState> FolderStates { get; set; } = new();
}

public enum ConflictPolicySetting
{
    Prompt,
    AlwaysRename,
    AlwaysOverwrite,
    AlwaysSkip
}
