using System.Windows;
using MediaSort.Services;

namespace MediaSort.Views;

public partial class ConflictDialog : Window
{
    public ConflictPolicy Choice { get; private set; } = ConflictPolicy.Skip;
    public bool ApplyToAll => ApplyAllCheck.IsChecked == true;

    public ConflictDialog(string fileName, string destFolder)
    {
        InitializeComponent();
        MessageText.Text = $"\"{fileName}\" already exists in:\n{destFolder}\n\nRename keeps both files. Overwrite replaces the existing file. Skip leaves the source file unchanged.";
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictPolicy.Rename;
        DialogResult = true;
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictPolicy.Overwrite;
        DialogResult = true;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConflictPolicy.Skip;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
