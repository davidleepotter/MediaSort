using System.Windows;
using MediaSort.Services;

namespace MediaSort.Views;

/// <summary>
/// Themed replacement for MessageBox with OK/Cancel semantics. Honors the
/// current app theme (dark/light) including the title-bar chrome via
/// WindowChrome.ApplyCurrentTheme.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string heading, string message,
                         string okText = "OK", string cancelText = "Cancel")
    {
        InitializeComponent();
        Title = heading;
        HeadingText.Text = heading;
        MessageText.Text = message;
        OkButton.Content = okText;
        CancelButton.Content = cancelText;
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    /// <summary>
    /// Convenience wrapper. Returns true if user clicked OK, false otherwise.
    /// </summary>
    public static bool Show(Window owner, string heading, string message,
                            string okText = "OK", string cancelText = "Cancel")
    {
        var dlg = new ConfirmDialog(heading, message, okText, cancelText) { Owner = owner };
        return dlg.ShowDialog() == true;
    }

    /// <summary>
    /// Variant with a "Don't ask again" checkbox. Returns (ok, dontAskAgain).
    /// dontAskAgain is only meaningful when ok==true.
    /// </summary>
    public static (bool ok, bool dontAskAgain) ShowWithSuppress(
        Window owner, string heading, string message,
        string okText = "OK", string cancelText = "Cancel")
    {
        var dlg = new ConfirmDialog(heading, message, okText, cancelText) { Owner = owner };
        dlg.DontAskAgainCheck.Visibility = Visibility.Visible;
        bool ok = dlg.ShowDialog() == true;
        bool dontAsk = dlg.DontAskAgainCheck.IsChecked == true;
        return (ok, dontAsk);
    }
}
