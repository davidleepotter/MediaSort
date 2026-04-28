using System.Windows;
using MediaSort.Services;

namespace MediaSort.Views;

public partial class KeyboardHelpWindow : Window
{
    public KeyboardHelpWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
