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
    Kind
}

public class SerializableDestination
{
    public string Name { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string HotKey { get; set; } = "None";
    public string Modifiers { get; set; } = "None";
}

public class AppSettings
{
    public string SourceFolder { get; set; } = "";
    public bool RecursiveScan { get; set; } = false;
    public ViewMode ViewMode { get; set; } = ViewMode.Details;
    public SortKey SortKey { get; set; } = SortKey.Name;
    public bool SortDescending { get; set; } = false;
    public List<SerializableDestination> Destinations { get; set; } = new();
}
