using System.Text.Json;
using MediaSort.Models;
using Xunit;

namespace MediaSort.Tests;

/// <summary>
/// SettingsService.Save / Load hard-code the path to
/// %APPDATA%\MediaSort\settings.json, so calling them in tests would clobber
/// the developer's real settings on every test run. Instead we exercise the
/// JSON contract directly with the same serializer options the service uses
/// (WriteIndented = true). This still proves that all properties round-trip
/// correctly — which is the real risk when adding/renaming settings.
///
/// If SettingsService ever grows a path-overload (Save(s, path) / Load(path))
/// these tests should switch to driving it through that surface.
/// </summary>
public class SettingsServiceRoundtripTests
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static AppSettings Roundtrip(AppSettings input)
    {
        var json = JsonSerializer.Serialize(input, Options);
        var output = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(output);
        return output!;
    }

    [Fact]
    public void Default_settings_roundtrip_unchanged()
    {
        var input = new AppSettings();
        var output = Roundtrip(input);

        Assert.Equal(input.RecursiveScan, output.RecursiveScan);
        Assert.Equal(input.IncludeHiddenFiles, output.IncludeHiddenFiles);
        Assert.Equal(input.ThemeOverride, output.ThemeOverride);
        Assert.Equal(input.AccentColor, output.AccentColor);
        Assert.Equal(input.ThumbnailSize, output.ThumbnailSize);
        Assert.Equal(input.AnimationDurationMs, output.AnimationDurationMs);
        Assert.Equal(input.AutoAdvanceAfterMove, output.AutoAdvanceAfterMove);
        Assert.Equal(input.ConfirmDestinationClick, output.ConfirmDestinationClick);
    }

    [Fact]
    public void Non_default_scalars_roundtrip()
    {
        var input = new AppSettings
        {
            SourceFolder = @"C:\Photos\2026",
            RecursiveScan = true,
            IncludeHiddenFiles = true,
            SortDescending = true,
            ThemeOverride = ThemeOverride.Light,
            AccentColor = "#FF0000",
            ThumbnailSize = 180,
            AnimationDurationMs = 250,
            AutoAdvanceAfterMove = false,
            ConfirmDestinationClick = false,
            DuplicateThreshold = 8,
            VideoVolume = 30,
            VideoMuted = true,
            Action = FileAction.Copy,
        };

        var output = Roundtrip(input);

        Assert.Equal(@"C:\Photos\2026", output.SourceFolder);
        Assert.True(output.RecursiveScan);
        Assert.True(output.IncludeHiddenFiles);
        Assert.True(output.SortDescending);
        Assert.Equal(ThemeOverride.Light, output.ThemeOverride);
        Assert.Equal("#FF0000", output.AccentColor);
        Assert.Equal(180, output.ThumbnailSize);
        Assert.Equal(250, output.AnimationDurationMs);
        Assert.False(output.AutoAdvanceAfterMove);
        Assert.False(output.ConfirmDestinationClick);
        Assert.Equal(8, output.DuplicateThreshold);
        Assert.Equal(30, output.VideoVolume);
        Assert.True(output.VideoMuted);
        Assert.Equal(FileAction.Copy, output.Action);
    }

    [Fact]
    public void Destinations_roundtrip_with_all_fields()
    {
        var input = new AppSettings
        {
            Destinations =
            {
                new SerializableDestination
                {
                    Name = "Keepers",
                    FolderPath = @"D:\Sorted\Keepers",
                    HotKey = "K",
                    Modifiers = "Ctrl",
                    KindFilter = "Image",
                    SubfolderTemplate = "{yyyy}/{MM}",
                    RenameTemplate = "{name}_{counter:000}",
                    ActionOverride = "Copy",
                    AccentColor = "#00FF00",
                },
                new SerializableDestination
                {
                    Name = "Trash",
                    FolderPath = @"D:\Sorted\Trash",
                    HotKey = "Delete",
                },
            },
        };

        var output = Roundtrip(input);

        Assert.Equal(2, output.Destinations.Count);

        var keep = output.Destinations[0];
        Assert.Equal("Keepers", keep.Name);
        Assert.Equal(@"D:\Sorted\Keepers", keep.FolderPath);
        Assert.Equal("K", keep.HotKey);
        Assert.Equal("Ctrl", keep.Modifiers);
        Assert.Equal("Image", keep.KindFilter);
        Assert.Equal("{yyyy}/{MM}", keep.SubfolderTemplate);
        Assert.Equal("{name}_{counter:000}", keep.RenameTemplate);
        Assert.Equal("Copy", keep.ActionOverride);
        Assert.Equal("#00FF00", keep.AccentColor);

        var trash = output.Destinations[1];
        Assert.Equal("Trash", trash.Name);
        Assert.Equal("Delete", trash.HotKey);
    }

    [Fact]
    public void Recents_and_favorites_roundtrip()
    {
        var input = new AppSettings
        {
            RecentSourceFolders = { @"C:\A", @"C:\B", @"C:\C" },
            Favorites = { @"D:\Fav1", @"D:\Fav2" },
        };

        var output = Roundtrip(input);

        Assert.Equal(new[] { @"C:\A", @"C:\B", @"C:\C" }, output.RecentSourceFolders);
        Assert.Equal(new[] { @"D:\Fav1", @"D:\Fav2" }, output.Favorites);
    }

    [Fact]
    public void Window_NaN_position_roundtrips_as_NaN()
    {
        // Brand-new install — window position is NaN. Must survive the JSON trip
        // because writing a real number would force first-launch placement off-screen.
        var input = new AppSettings();
        var output = Roundtrip(input);

        Assert.True(double.IsNaN(output.WindowLeft));
        Assert.True(double.IsNaN(output.WindowTop));
        Assert.True(double.IsNaN(output.WindowWidth));
        Assert.True(double.IsNaN(output.WindowHeight));
    }

    [Fact]
    public void Pane_order_and_visibility_roundtrip()
    {
        var input = new AppSettings
        {
            PaneOrder = "Destinations,Preview,Source",
            SourcePaneVisible = false,
            PreviewPaneVisible = true,
            DestinationsPaneVisible = false,
            LeftPanelWidth = 250,
            RightPanelWidth = 350,
        };

        var output = Roundtrip(input);

        Assert.Equal("Destinations,Preview,Source", output.PaneOrder);
        Assert.False(output.SourcePaneVisible);
        Assert.True(output.PreviewPaneVisible);
        Assert.False(output.DestinationsPaneVisible);
        Assert.Equal(250, output.LeftPanelWidth);
        Assert.Equal(350, output.RightPanelWidth);
    }
}
