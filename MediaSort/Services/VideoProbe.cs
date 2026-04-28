using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace MediaSort.Services;

/// <summary>
/// Reads video dimensions via libVLC media parsing.
/// Shares a single LibVLC instance across probes to avoid repeated init cost.
/// </summary>
public static class VideoProbe
{
    private static LibVLC? _libVlc;
    private static readonly object _lock = new();

    private static LibVLC Instance()
    {
        if (_libVlc != null) return _libVlc;
        lock (_lock)
        {
            if (_libVlc == null)
            {
                Core.Initialize();
                // --quiet avoids spamming stderr; --no-video keeps the probe lightweight.
                _libVlc = new LibVLC("--quiet", "--no-video", "--no-audio");
            }
            return _libVlc;
        }
    }

    /// <summary>
    /// Probe a video file for its (width, height). Returns (0,0) on failure or timeout.
    /// </summary>
    public static (int width, int height) TryReadVideoDimensions(string path, int timeoutMs = 3000)
    {
        var (w, h, _) = TryReadVideoInfo(path, timeoutMs);
        return (w, h);
    }

    /// <summary>
    /// Probe a video file for (width, height, durationSeconds). Returns zeros on failure.
    /// </summary>
    public static (int width, int height, double durationSeconds) TryReadVideoInfo(string path, int timeoutMs = 3000)
    {
        try
        {
            var libVlc = Instance();
            using var media = new Media(libVlc, new Uri(path));

            var task = media.Parse(MediaParseOptions.ParseLocal, timeoutMs);
            task.Wait(timeoutMs + 500);

            var videoTrack = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
            int w = 0, h = 0;
            if (videoTrack.TrackType == TrackType.Video)
            {
                var v = videoTrack.Data.Video;
                w = (int)v.Width;
                h = (int)v.Height;
            }

            // Duration is in milliseconds in libVLC
            double dur = media.Duration > 0 ? media.Duration / 1000.0 : 0;
            return (w, h, dur);
        }
        catch
        {
            return (0, 0, 0);
        }
    }
}
