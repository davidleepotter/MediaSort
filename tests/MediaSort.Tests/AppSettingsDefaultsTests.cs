using MediaSort.Models;
using Xunit;

namespace MediaSort.Tests;

/// <summary>
/// Locks in the documented default values for <see cref="AppSettings"/>.
/// These defaults are part of the app's user-visible contract — changing one
/// silently can flip behavior for every existing user on upgrade. If a test
/// here fails, update both the code default AND the test deliberately.
/// </summary>
public class AppSettingsDefaultsTests
{
    [Fact]
    public void Scanning_defaults_are_safe()
    {
        var s = new AppSettings();
        Assert.False(s.RecursiveScan);
        Assert.False(s.IncludeHiddenFiles);
        Assert.False(s.SortDescending);
    }

    [Fact]
    public void Workflow_defaults_match_expected_ux()
    {
        var s = new AppSettings();
        Assert.True(s.AutoAdvanceAfterMove);
        Assert.True(s.ConfirmDestinationClick);
        Assert.Equal(FileAction.Move, s.Action);
        Assert.Equal(ConflictPolicySetting.Prompt, s.ConflictPolicy);
    }

    [Fact]
    public void Theme_defaults_match_design()
    {
        var s = new AppSettings();
        Assert.Equal(ThemeOverride.Dark, s.ThemeOverride);
        Assert.Equal("#2D7BD4", s.AccentColor);
        Assert.Equal(420, s.AnimationDurationMs);
        Assert.Equal(120, s.ThumbnailSize);
    }

    [Fact]
    public void Collection_defaults_are_empty_not_null()
    {
        var s = new AppSettings();
        Assert.NotNull(s.Destinations);
        Assert.Empty(s.Destinations);
        Assert.NotNull(s.FolderStates);
        Assert.Empty(s.FolderStates);
        Assert.NotNull(s.RecentSourceFolders);
        Assert.Empty(s.RecentSourceFolders);
        Assert.NotNull(s.Favorites);
        Assert.Empty(s.Favorites);
    }

    [Fact]
    public void Filter_defaults_show_everything()
    {
        var s = new AppSettings();
        Assert.Equal(DateFilterMode.All, s.DateFilter);
        Assert.Equal(AspectGroup.All, s.AspectGroupFilter);
        Assert.Equal(SortKey.Name, s.SortKey);
        Assert.Equal(ViewMode.Details, s.ViewMode);
    }

    [Fact]
    public void Video_defaults()
    {
        var s = new AppSettings();
        Assert.Equal(60, s.VideoVolume);
        Assert.False(s.VideoMuted);
    }

    [Fact]
    public void Window_defaults_signal_first_run()
    {
        var s = new AppSettings();
        // NaN means "no saved position yet" — main window will use system placement.
        Assert.True(double.IsNaN(s.WindowLeft));
        Assert.True(double.IsNaN(s.WindowTop));
        Assert.True(double.IsNaN(s.WindowWidth));
        Assert.True(double.IsNaN(s.WindowHeight));
        Assert.False(s.WindowMaximized);
    }

    [Fact]
    public void Pane_defaults()
    {
        var s = new AppSettings();
        Assert.Equal("Source,Preview,Destinations", s.PaneOrder);
        Assert.True(s.SourcePaneVisible);
        Assert.True(s.PreviewPaneVisible);
        Assert.True(s.DestinationsPaneVisible);
    }

    [Fact]
    public void DuplicateThreshold_default()
    {
        var s = new AppSettings();
        Assert.Equal(4, s.DuplicateThreshold);
    }
}
