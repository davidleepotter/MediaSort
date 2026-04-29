using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MediaSort.Services;

/// <summary>
/// (#16) Watches a set of folder paths for "offline" transitions:
///   * Local drives that get unmounted (USB pulled, SD card removed).
///   * Network shares that lose connectivity.
///   * Removable media that pop up after being plugged in.
/// Combines two strategies:
///   1. WM_DEVICECHANGE Windows message hook (instant for USB / drive arrivals
///      and removals on the local machine).
///   2. Periodic poll via Directory.Exists (catches network share loss and
///      generally robust against missed messages).
/// Raises <see cref="StatusChanged"/> on the UI thread.
/// </summary>
public class VolumeMonitor : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, bool> _state = new(StringComparer.OrdinalIgnoreCase);
    private HwndSource? _hwndSource;
    private bool _disposed;

    /// <summary>
    /// Raised whenever a watched path's online/offline state flips, OR when a
    /// device-change message comes in (so subscribers can re-poll immediately).
    /// </summary>
    public event Action<VolumeStatusChange>? StatusChanged;

    public VolumeMonitor()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>
    /// Hook into a WPF window so we receive WM_DEVICECHANGE. Safe to call once
    /// from MainWindow after SourceInitialized.
    /// </summary>
    public void Attach(System.Windows.Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);
        _timer.Start();
    }

    /// <summary>Begin watching a path (folder). Idempotent.</summary>
    public void Watch(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!_state.ContainsKey(path))
        {
            _state[path] = SafeExists(path);
        }
    }

    /// <summary>Stop watching a path.</summary>
    public void Unwatch(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _state.Remove(path);
    }

    /// <summary>Replace the watch list atomically.</summary>
    public void SetWatchList(IEnumerable<string> paths)
    {
        var keep = new HashSet<string>(paths.Where(p => !string.IsNullOrWhiteSpace(p)),
                                       StringComparer.OrdinalIgnoreCase);
        // remove obsolete
        foreach (var k in _state.Keys.ToList())
            if (!keep.Contains(k)) _state.Remove(k);
        // add new
        foreach (var p in keep)
            if (!_state.ContainsKey(p)) _state[p] = SafeExists(p);
    }

    /// <summary>True if the given path was online at the most recent check.</summary>
    public bool IsOnline(string path)
        => _state.TryGetValue(path, out var v) ? v : SafeExists(path);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE)
        {
            int evt = wParam.ToInt32();
            if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE || evt == DBT_DEVNODES_CHANGED)
            {
                // Re-poll immediately on any device-change event.
                Poll();
            }
        }
        return IntPtr.Zero;
    }

    private void Poll()
    {
        if (_disposed) return;
        // Snapshot to avoid mutation during enumeration.
        foreach (var path in _state.Keys.ToList())
        {
            bool wasOnline = _state[path];
            bool nowOnline = SafeExists(path);
            if (wasOnline != nowOnline)
            {
                _state[path] = nowOnline;
                StatusChanged?.Invoke(new VolumeStatusChange(path, nowOnline));
            }
        }
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _timer.Stop(); } catch { }
        try { _hwndSource?.RemoveHook(WndProc); } catch { }
        _hwndSource = null;
    }
}

public readonly struct VolumeStatusChange
{
    public string Path { get; }
    public bool IsOnline { get; }
    public VolumeStatusChange(string path, bool online) { Path = path; IsOnline = online; }
}
