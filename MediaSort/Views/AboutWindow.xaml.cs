using System.Windows;
using MediaSort.Services;

namespace MediaSort.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
        VersionText.Text = $"Version {VersionInfo.GetVersion()}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
