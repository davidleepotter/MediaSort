using System;
using System.IO;

namespace MediaSort.Services;

/// <summary>
/// Writes unhandled exceptions to %LOCALAPPDATA%/MediaSort/crash.log with timestamps.
/// Wire from App.OnStartup with AppDomain + Dispatcher hooks.
/// </summary>
public static class CrashLogger
{
    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaSort");
    private static readonly string LogPath = Path.Combine(LogDir, "crash.log");

    public static string LogFilePath => LogPath;

    public static void Log(Exception ex, string source = "")
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            using var sw = File.AppendText(LogPath);
            sw.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  source={source} ====");
            sw.WriteLine(ex.ToString());
            sw.WriteLine();
        }
        catch
        {
            // Crash logger must never throw
        }
    }
}
