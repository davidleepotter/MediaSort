using System.Collections.ObjectModel;
using System.Windows;
using MediaSort.Models;

namespace MediaSort.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings, ObservableCollection<DestinationButton> destinations)
    {
        InitializeComponent();
        _settings = settings;

        SourceFolderBox.Text = settings.SourceFolder;
        RecursiveCheck.IsChecked = settings.RecursiveScan;
        ViewModeCombo.SelectedIndex = (int)settings.ViewMode;
        DestinationsList.ItemsSource = destinations;
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Choose the source folder of media files"
        };
        if (!string.IsNullOrWhiteSpace(SourceFolderBox.Text) && System.IO.Directory.Exists(SourceFolderBox.Text))
            dlg.SelectedPath = SourceFolderBox.Text;
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SourceFolderBox.Text = dlg.SelectedPath;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        _settings.SourceFolder = SourceFolderBox.Text.Trim();
        _settings.RecursiveScan = RecursiveCheck.IsChecked == true;
        _settings.ViewMode = (ViewMode)ViewModeCombo.SelectedIndex;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
