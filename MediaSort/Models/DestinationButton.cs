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

    public string DisplayLabel => string.IsNullOrWhiteSpace(Name)
        ? $"[{HotKeyDisplay}]"
        : $"{Name}  [{HotKeyDisplay}]";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
