using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MediaSort.Models;

public class DestinationButton : INotifyPropertyChanged
{
    private string _name = "New Destination";
    private string _folderPath = "";
    private Key _hotKey = Key.None;
    private ModifierKeys _modifiers = ModifierKeys.None;
    private string _kindFilter = "";
    private string _subfolderTemplate = "";
    private string _renameTemplate = "";
    private int _itemCount;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public string FolderPath
    {
        get => _folderPath;
        set { _folderPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public Key HotKey
    {
        get => _hotKey;
        set { _hotKey = value; OnPropertyChanged(); OnPropertyChanged(nameof(HotKeyDisplay)); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public ModifierKeys Modifiers
    {
        get => _modifiers;
        set { _modifiers = value; OnPropertyChanged(); OnPropertyChanged(nameof(HotKeyDisplay)); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    /// <summary>"" = any kind. "Image" or "Video" restricts which media is accepted.</summary>
    public string KindFilter
    {
        get => _kindFilter;
        set { _kindFilter = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(BadgeText)); }
    }

    /// <summary>Optional subfolder template like "{date:yyyy-MM}" or "Photos/{date:yyyy}".</summary>
    public string SubfolderTemplate
    {
        get => _subfolderTemplate;
        set { _subfolderTemplate = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>Optional file rename template applied at move time.</summary>
    public string RenameTemplate
    {
        get => _renameTemplate;
        set { _renameTemplate = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>Number of files currently in the destination folder. Refreshed lazily.</summary>
    public int ItemCount
    {
        get => _itemCount;
        set { if (_itemCount == value) return; _itemCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(BadgeText)); }
    }

    public string HotKeyDisplay
    {
        get
        {
            if (HotKey == Key.None) return "(no key)";
            var s = "";
            if ((Modifiers & ModifierKeys.Control) != 0) s += "Ctrl+";
            if ((Modifiers & ModifierKeys.Alt) != 0) s += "Alt+";
            if ((Modifiers & ModifierKeys.Shift) != 0) s += "Shift+";
            s += HotKey.ToString();
            return s;
        }
    }

    public string BadgeText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (ItemCount > 0) parts.Add($"{ItemCount} file{(ItemCount == 1 ? "" : "s")}");
            if (!string.IsNullOrEmpty(KindFilter)) parts.Add($"{KindFilter} only");
            return string.Join(" · ", parts);
        }
    }

    public string DisplayLabel => string.IsNullOrWhiteSpace(Name)
        ? $"[{HotKeyDisplay}]"
        : $"{Name}  [{HotKeyDisplay}]";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
