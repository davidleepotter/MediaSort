using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaSort.Models;
using MediaSort.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace MediaSort.Views;

public partial class DestinationEditor : Window
{
    private readonly DestinationButton _dest;

    public DestinationEditor(DestinationButton dest)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
        _dest = dest;

        NameBox.Text = dest.Name;
        FolderBox.Text = dest.FolderPath;
        UpdateHotkeyDisplay(dest.HotKey, dest.Modifiers);
        SubfolderBox.Text = dest.SubfolderTemplate;
        RenameBox.Text = dest.RenameTemplate;
        KindFilterCombo.SelectedIndex = dest.KindFilter switch
        {
            "Image" => 1,
            "Video" => 2,
            _ => 0,
        };
        // (#17) Preselect the action override; null = inherit (index 0)
        ActionOverrideCombo.SelectedIndex = dest.ActionOverride switch
        {
            FileAction.Move => 1,
            FileAction.Copy => 2,
            FileAction.Delete => 3,
            _ => 0,
        };

        // (#10) Per-destination tint color (empty = no tint)
        AccentBox.Text = dest.AccentColor ?? "";
        UpdateAccentSwatch();
    }

    private void AccentBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateAccentSwatch();

    private void UpdateAccentSwatch()
    {
        if (AccentSwatch == null) return;
        var t = AccentBox?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(t))
        {
            AccentSwatch.Background = System.Windows.Media.Brushes.Transparent;
            return;
        }
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(t);
            AccentSwatch.Background = new SolidColorBrush(c);
        }
        catch
        {
            AccentSwatch.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            AllowFullOpen = true
        };
        // Seed with current value if valid
        var t = AccentBox.Text?.Trim() ?? "";
        if (!string.IsNullOrEmpty(t))
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(t);
                dlg.Color = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
            }
            catch { /* ignore parse errors */ }
        }
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            AccentBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }

    private void ClearColor_Click(object sender, RoutedEventArgs e)
    {
        AccentBox.Text = "";
    }

    private Key _pendingKey;
    private ModifierKeys _pendingModifiers;

    private void UpdateHotkeyDisplay(Key key, ModifierKeys mods)
    {
        _pendingKey = key;
        _pendingModifiers = mods;
        if (key == Key.None) { HotkeyBox.Text = "(none)"; return; }
        var s = "";
        if ((mods & ModifierKeys.Control) != 0) s += "Ctrl+";
        if ((mods & ModifierKeys.Alt) != 0) s += "Alt+";
        if ((mods & ModifierKeys.Shift) != 0) s += "Shift+";
        s += MediaSort.Models.DestinationButton.FormatKey(key);
        HotkeyBox.Text = s;
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Back || key == Key.Delete)
        {
            UpdateHotkeyDisplay(Key.None, ModifierKeys.None);
            return;
        }

        // Ignore raw modifier-only presses
        if (key == Key.LeftCtrl || key == Key.RightCtrl
            || key == Key.LeftAlt || key == Key.RightAlt
            || key == Key.LeftShift || key == Key.RightShift
            || key == Key.LWin || key == Key.RWin
            || key == Key.Tab)
        {
            return;
        }

        UpdateHotkeyDisplay(key, Keyboard.Modifiers);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose a destination folder"
        };
        if (!string.IsNullOrWhiteSpace(FolderBox.Text) && System.IO.Directory.Exists(FolderBox.Text))
            dlg.SelectedPath = FolderBox.Text;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            FolderBox.Text = dlg.SelectedPath;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _dest.Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Destination" : NameBox.Text.Trim();
        _dest.FolderPath = FolderBox.Text.Trim();
        _dest.HotKey = _pendingKey;
        _dest.Modifiers = _pendingModifiers;
        _dest.KindFilter = KindFilterCombo.SelectedIndex switch
        {
            1 => "Image",
            2 => "Video",
            _ => "",
        };
        _dest.SubfolderTemplate = SubfolderBox.Text.Trim();
        _dest.RenameTemplate = RenameBox.Text.Trim();
        // (#17)
        _dest.ActionOverride = ActionOverrideCombo.SelectedIndex switch
        {
            1 => FileAction.Move,
            2 => FileAction.Copy,
            3 => FileAction.Delete,
            _ => (FileAction?)null,
        };
        // (#10) Per-destination tint color
        _dest.AccentColor = AccentBox.Text?.Trim() ?? "";
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
