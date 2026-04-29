using System.Windows;
using MediaSort.Services;

namespace MediaSort.Views;

/// <summary>
/// (#11) Themed popup that shows the per-action breakdown of the running
/// <see cref="SessionStats"/>. Reset wipes counters in place.
/// </summary>
public partial class SessionStatsDialog : Window
{
    private readonly SessionStats _stats;

    public SessionStatsDialog(SessionStats stats)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
        _stats = stats;
        Refresh();
    }

    private void Refresh()
    {
        StartedText.Text = $"Started {_stats.SessionStarted:yyyy-MM-dd HH:mm}";
        MovedCountText.Text   = _stats.MovedCount.ToString("N0");
        MovedBytesText.Text   = SessionStats.FormatBytes(_stats.MovedBytes);
        CopiedCountText.Text  = _stats.CopiedCount.ToString("N0");
        CopiedBytesText.Text  = SessionStats.FormatBytes(_stats.CopiedBytes);
        DeletedCountText.Text = _stats.DeletedCount.ToString("N0");
        DeletedBytesText.Text = SessionStats.FormatBytes(_stats.DeletedBytes);
        TotalCountText.Text   = _stats.TotalCount.ToString("N0");
        TotalBytesText.Text   = SessionStats.FormatBytes(_stats.TotalBytes);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _stats.Reset();
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
