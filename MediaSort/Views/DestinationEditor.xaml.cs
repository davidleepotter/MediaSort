using System.Windows;
using System.Windows.Input;
using MediaSort.Models;
using MediaSort.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

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
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
