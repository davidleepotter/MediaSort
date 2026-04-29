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
    private MediaSort.Models.FileAction? _actionOverride;
    private int _itemCount;
    private string _flashBadge = "";
    private double _flashOpacity = 0;
    private bool _isOffline;

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

    /// <summary>
    /// (#17) Per-destination action override. When set, this destination will always
    /// Move/Copy/Delete regardless of the toolbar Action selector. null = inherit
    /// (the global toolbar setting wins).
    /// </summary>
    public MediaSort.Models.FileAction? ActionOverride
    {
        get => _actionOverride;
        set { _actionOverride = value; OnPropertyChanged(); OnPropertyChanged(nameof(BadgeText)); }
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
            s += FormatKey(HotKey);
            return s;
        }
    }

    /// <summary>
    /// Render a WPF Key as a human-friendly label.
    /// D0–D9 → "0"–"9", NumPad0–NumPad9 → "Num 0"–"Num 9",
    /// common Oem* keys → their printed character.
    /// </summary>
    public static string FormatKey(Key k)
    {
        // Top-row digits
        if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
        // Numeric keypad
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return "Num " + ((int)(k - Key.NumPad0)).ToString();
        // Function keys read fine as F1–F24 already.
        return k switch
        {
            Key.OemQuestion    => "?",
            Key.OemPeriod      => ".",
            Key.OemComma       => ",",
            Key.OemSemicolon   => ";",
            Key.OemQuotes      => "'",
            Key.OemMinus       => "-",
            Key.OemPlus        => "=",
            Key.OemTilde       => "`",
            Key.OemBackslash   => "\\",
            Key.OemPipe        => "|",
            Key.OemOpenBrackets  => "[",
            Key.OemCloseBrackets => "]",
            Key.Add       => "Num +",
            Key.Subtract  => "Num -",
            Key.Multiply  => "Num *",
            Key.Divide    => "Num /",
            Key.Decimal   => "Num .",
            Key.Space     => "Space",
            Key.Return    => "Enter",
            Key.Escape    => "Esc",
            Key.Back      => "Backspace",
            Key.PageUp    => "PgUp",
            Key.PageDown  => "PgDn",
            Key.Up        => "↑",
            Key.Down      => "↓",
            Key.Left      => "←",
            Key.Right     => "→",
            _ => k.ToString()
        };
    }

    public string BadgeText
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if (ItemCount > 0) parts.Add($"{ItemCount} file{(ItemCount == 1 ? "" : "s")}");
            if (!string.IsNullOrEmpty(KindFilter)) parts.Add($"{KindFilter} only");
            if (ActionOverride.HasValue) parts.Add($"always {ActionOverride.Value}");
            return string.Join(" · ", parts);
        }
    }

    public string DisplayLabel => string.IsNullOrWhiteSpace(Name)
        ? $"[{HotKeyDisplay}]"
        : $"{Name}  [{HotKeyDisplay}]";

    /// <summary>
    /// Transient "+N" badge shown on the destination after a successful move.
    /// MainWindow sets this and animates FlashOpacity 0⁡1⁡0 over a short window.
    /// </summary>
    public string FlashBadge
    {
        get => _flashBadge;
        set { _flashBadge = value ?? ""; OnPropertyChanged(); }
    }

    public double FlashOpacity
    {
        get => _flashOpacity;
        set { _flashOpacity = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// (#16) True when the underlying folder is currently unreachable
    /// (e.g. USB unplugged, network share dropped). Surfaces an "Offline"
    /// indicator on the destination button.
    /// </summary>
    public bool IsOffline
    {
        get => _isOffline;
        set
        {
            if (_isOffline == value) return;
            _isOffline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(StatusGlyphTooltip));
            OnPropertyChanged(nameof(BadgeOpacity));
        }
    }

    /// <summary>⚠ if offline, empty otherwise. Bound in the destination row.</summary>
    public string StatusGlyph => _isOffline ? "⚠ Offline" : "";

    public string StatusGlyphTooltip => _isOffline
        ? "This folder is currently unreachable. Reconnect the drive or network share to use this destination."
        : "";

    /// <summary>Dim the row when offline.</summary>
    public double BadgeOpacity => _isOffline ? 0.5 : 1.0;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
