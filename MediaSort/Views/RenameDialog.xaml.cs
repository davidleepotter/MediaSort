using System;
using System.IO;
using System.Linq;
using System.Windows;
using MediaSort.Services;

namespace MediaSort.Views;

public partial class RenameDialog : Window
{
    private readonly string _originalFullPath;
    private readonly string _originalExtension;

    /// <summary>The new filename WITH extension after the user accepts. Null if cancelled.</summary>
    public string? NewFileName { get; private set; }

    public RenameDialog(string fullPath)
    {
        InitializeComponent();
        _originalFullPath = fullPath;
        _originalExtension = Path.GetExtension(fullPath); // includes the dot
        var fileName = Path.GetFileName(fullPath);

        CurrentNameText.Text = $"Current: {fileName}";

        // Default: edit name without extension; cursor selects just the stem.
        NameBox.Text = Path.GetFileNameWithoutExtension(fullPath);
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };

        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
    }

    private void EditExtension_Toggled(object sender, RoutedEventArgs e)
    {
        if (EditExtensionCheck.IsChecked == true)
        {
            // Switch to editing the full filename (with extension).
            var current = NameBox.Text;
            if (!current.EndsWith(_originalExtension, StringComparison.OrdinalIgnoreCase))
                NameBox.Text = current + _originalExtension;
        }
        else
        {
            // Switch back to stem-only.
            NameBox.Text = Path.GetFileNameWithoutExtension(NameBox.Text);
        }
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private string ComposeFinalName()
    {
        var typed = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(typed)) return "";
        if (EditExtensionCheck.IsChecked == true) return typed;
        return typed + _originalExtension;
    }

    private string? ValidateName(string proposed)
    {
        if (string.IsNullOrWhiteSpace(proposed)) return "Name cannot be empty.";
        if (proposed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "Name contains invalid characters: " + string.Concat(Path.GetInvalidFileNameChars().Where(c => proposed.Contains(c)));
        if (proposed.Length > 240) return "Name is too long.";

        var dir = Path.GetDirectoryName(_originalFullPath) ?? "";
        var newPath = Path.Combine(dir, proposed);
        if (string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(_originalFullPath), StringComparison.OrdinalIgnoreCase))
            return null; // unchanged — caller will treat as cancel
        if (File.Exists(newPath) || Directory.Exists(newPath))
            return $"A file named \"{proposed}\" already exists in this folder.";
        return null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var proposed = ComposeFinalName();
        var err = ValidateName(proposed);
        if (err != null) { ErrorText.Text = err; return; }

        if (string.Equals(proposed, Path.GetFileName(_originalFullPath), StringComparison.OrdinalIgnoreCase))
        {
            // No change — close as cancel so caller does nothing.
            DialogResult = false;
            return;
        }

        NewFileName = proposed;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void NameBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { Ok_Click(sender, e); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape) { Cancel_Click(sender, e); e.Handled = true; }
    }
}
