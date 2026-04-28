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
        try
        {
            var libVlc = Instance();
            using var media = new Media(libVlc, new Uri(path));

            // ParseAsync blocks until parsing completes or times out.
            var task = media.Parse(MediaParseOptions.ParseLocal, timeoutMs);
            task.Wait(timeoutMs + 500);

            var videoTrack = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
            if (videoTrack.TrackType != TrackType.Video) return (0, 0);

            var v = videoTrack.Data.Video;
            int w = (int)v.Width;
            int h = (int)v.Height;
            return (w, h);
        }
        catch
        {
            return (0, 0);
        }
    }
}
