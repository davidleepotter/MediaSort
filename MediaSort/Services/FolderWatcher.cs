using System;
using System.IO;
using System.Windows.Threading;
using MediaSort.Models;

namespace MediaSort.Services;

/// <summary>
/// Watches the source folder and raises a debounced Changed event when files
/// are added, removed, or renamed. Caller is expected to refresh the source list.
/// </summary>
public class FolderWatcher : IDisposable
{
    private FileSystemWatcher? _fsw;
    private readonly DispatcherTimer _debounce;
    private readonly Dispatcher _uiDispatcher;

    public event Action? Changed;

    public FolderWatcher(Dispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, uiDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Changed?.Invoke(); };
    }

    public void Watch(string folder, bool recursive)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        try
        {
            _fsw = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _fsw.Created += OnAny;
            _fsw.Deleted += OnAny;
            _fsw.Renamed += OnAny;
            _fsw.Changed += OnAny;
        }
        catch
        {
            _fsw = null; // best-effort
        }
    }

    private void OnAny(object sender, FileSystemEventArgs e)
    {
        // Filter to media extensions to avoid spurious refreshes
        var ext = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (!MediaFormats.AllExtensions.Contains(ext)) return;

        _uiDispatcher.BeginInvoke(new Action(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        }));
    }

    public void Stop()
    {
        if (_fsw != null)
        {
            try { _fsw.EnableRaisingEvents = false; } catch { }
            try { _fsw.Dispose(); } catch { }
            _fsw = null;
        }
        _debounce.Stop();
    }

    public void Dispose() => Stop();
}
