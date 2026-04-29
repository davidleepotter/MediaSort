using System;
using System.Threading;
using System.Windows;
using MediaSort.Services;

namespace MediaSort.Views;

public partial class ProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();

    public CancellationToken Token => _cts.Token;

    public ProgressDialog(string header, string detail)
    {
        InitializeComponent();
        HeaderText.Text = header;
        DetailText.Text = detail;
        SourceInitialized += (_, _) => WindowChrome.ApplyCurrentTheme(this);
    }

    public void ReportProgress(long bytesDone, long bytesTotal)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ReportProgress(bytesDone, bytesTotal)));
            return;
        }
        if (bytesTotal <= 0)
        {
            Bar.IsIndeterminate = true;
            StatusText.Text = FormatBytes(bytesDone);
            return;
        }
        Bar.IsIndeterminate = false;
        var pct = (double)bytesDone / bytesTotal * 100.0;
        Bar.Value = Math.Clamp(pct, 0, 100);
        StatusText.Text = $"{FormatBytes(bytesDone)} / {FormatBytes(bytesTotal)}  ({pct:0.0}%)";
    }

    /// <summary>
    /// Count-style progress (e.g. "42 / 1938") used by Find Duplicates and other
    /// item-count flows. Avoids the bytes-formatted label that confuses users
    /// when the units are images, not bytes.
    /// </summary>
    public void ReportCount(int done, int total)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ReportCount(done, total)));
            return;
        }
        if (total <= 0)
        {
            Bar.IsIndeterminate = true;
            StatusText.Text = $"{done}";
            return;
        }
        Bar.IsIndeterminate = false;
        var pct = (double)done / total * 100.0;
        Bar.Value = Math.Clamp(pct, 0, 100);
        StatusText.Text = $"{done} / {total}  ({pct:0.0}%)";
    }

    public void SetDetail(string detail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => SetDetail(detail)));
            return;
        }
        DetailText.Text = detail;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        StatusText.Text = "Cancelling…";
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
    }

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024L * 1024) return $"{b / 1024.0:0.0} KB";
        if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):0.0} MB";
        return $"{b / (1024.0 * 1024 * 1024):0.00} GB";
    }
}
