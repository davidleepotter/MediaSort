using System.Windows;
using MediaSort.Services;

namespace MediaSort.Views;

public enum DeleteChoice
{
    Cancel,
    RecycleBin,
    Permanent
}

/// <summary>
/// Themed delete-confirmation dialog offering three choices: send to the Recycle
/// Bin (default, recoverable), permanently delete (not recoverable), or cancel.
/// Mirrors <see cref="ConfirmDialog"/> styling so it matches the rest of the app.
/// </summary>
public partial class DeleteChoiceDialog : Window
{
    public DeleteChoice Choice { get; private set; } = DeleteChoice.Cancel;

    public DeleteChoiceDialog(string heading, string message)
    {
        InitializeComponent();
        Title = heading;
        HeadingText.Text = heading;
        MessageText.Text = message;
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
    }

    /// <summary>
    /// Show the dialog and return the user's choice. Recycle Bin is the default
    /// (Enter key); Cancel is wired to Esc/IsCancel.
    /// </summary>
    public static DeleteChoice Show(Window owner, string heading, string message)
    {
        var dlg = new DeleteChoiceDialog(heading, message) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Choice;
    }

    private void Recycle_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteChoice.RecycleBin;
        DialogResult = true;
        Close();
    }

    private void Permanent_Click(object sender, RoutedEventArgs e)
    {
        // Second-level guard: confirm permanent delete since it's not recoverable.
        var confirm = ConfirmDialog.Show(this,
            "Delete Permanently",
            "These files will be permanently deleted and CANNOT be recovered. Are you sure?",
            "Delete Permanently",
            "Cancel");
        if (!confirm) return;

        Choice = DeleteChoice.Permanent;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = DeleteChoice.Cancel;
        DialogResult = false;
        Close();
    }
}
