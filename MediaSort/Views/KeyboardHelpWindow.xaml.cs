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

    /// <summary>
    /// Open the help dialog scrolled to the "Destinations &amp; Flow" section.
    /// </summary>
    public void ShowFlowSection()
    {
        if (NavFlow != null) NavFlow.IsChecked = true;
    }

    private void NavShortcuts_Checked(object sender, RoutedEventArgs e)
    {
        if (ShortcutsView == null || FlowView == null) return;
        ShortcutsView.Visibility = Visibility.Visible;
        FlowView.Visibility = Visibility.Collapsed;
    }

    private void NavFlow_Checked(object sender, RoutedEventArgs e)
    {
        if (ShortcutsView == null || FlowView == null) return;
        ShortcutsView.Visibility = Visibility.Collapsed;
        FlowView.Visibility = Visibility.Visible;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
