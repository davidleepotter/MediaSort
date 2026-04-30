using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MediaSort.Services;

/// <summary>
/// Single-instance enforcement for the MediaSort WPF app.
///
/// Pattern:
///   1. App.OnStartup calls <see cref="TryAcquire"/>. The first launch acquires
///      a per-user named <see cref="Mutex"/> and starts a background named-pipe
///      server that listens for "activate" pings from later launches.
///   2. A second launch fails to acquire the mutex, sends "activate" over the
///      pipe to the running instance, and exits before WPF creates a window.
///   3. The running instance, on receiving the ping, marshals to the UI thread,
///      restores the window if minimized, and brings it to the foreground.
///
/// Names are scoped to the current Windows user (via SID) so two different
/// users on the same machine can each run their own MediaSort \u2014 only one
/// per user is enforced.
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;
    private static CancellationTokenSource? _pipeCts;
    private static string MutexName => $"Local\\MediaSort.Singleton.{CurrentUserSid}";
    private static string PipeName => $"MediaSort.Activate.{CurrentUserSid}";

    private static string CurrentUserSid
    {
        get
        {
            try { return WindowsIdentity.GetCurrent().User?.Value ?? "anon"; }
            catch { return "anon"; }
        }
    }

    /// <summary>
    /// Try to become the single running instance.
    /// Returns true if this process is the first instance and should continue
    /// startup. Returns false if another instance is already running \u2014 the
    /// caller should signal it (already done by this method) and exit.
    /// </summary>
    public static bool TryAcquire()
    {
        bool createdNew;
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (Exception ex)
        {
            // If for any reason the mutex can't be created (extremely rare \u2014 e.g.
            // system-level security restriction), fail open: allow this instance
            // to run. Better to have two windows than no app at all.
            CrashLogger.Info($"single-instance:mutex FAIL {ex.GetType().Name}: {ex.Message} (failing open)");
            return true;
        }

        if (createdNew)
        {
            // We own the slot. Start a pipe server so future launches can reach us.
            StartPipeServer();
            return true;
        }

        // Another instance is already running. Send an activate ping and exit.
        TrySignalActivate();
        return false;
    }

    /// <summary>Release the mutex and stop the pipe server. Called from App.OnExit.</summary>
    public static void Release()
    {
        try { _pipeCts?.Cancel(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }

    // ---- Pipe server (running in the first instance) ----------------------

    private static void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        var ct = _pipeCts.Token;

        // One long-lived background task that loops accepting connections.
        // Each client connection is short (one-line "ACTIVATE" message) so we
        // don't bother with a fan-out worker pool.
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.Equals(line, "ACTIVATE", StringComparison.OrdinalIgnoreCase))
                    {
                        ActivateMainWindowOnUiThread();
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    // Don't let a transient pipe error tear down the loop.
                    CrashLogger.Info($"single-instance:pipe-server tick FAIL {ex.GetType().Name}: {ex.Message}");
                    try { await Task.Delay(250, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }, ct);
    }

    private static void ActivateMainWindowOnUiThread()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var w = app.MainWindow;
                    if (w == null) return;
                    if (w.WindowState == WindowState.Minimized)
                        w.WindowState = WindowState.Normal;
                    if (!w.IsVisible) w.Show();
                    w.Activate();
                    // TopMost flicker is the most reliable foreground-bringer
                    // when another process owns the foreground window.
                    bool wasTop = w.Topmost;
                    w.Topmost = true;
                    w.Topmost = wasTop;
                    w.Focus();
                }
                catch (Exception ex)
                {
                    CrashLogger.Info($"single-instance:activate FAIL {ex.GetType().Name}: {ex.Message}");
                }
            }));
        }
        catch (Exception ex)
        {
            CrashLogger.Info($"single-instance:dispatch-activate FAIL {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- Pipe client (running in the SECOND launch, about to exit) --------

    private static void TrySignalActivate()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            // Short timeout \u2014 if the running instance is wedged, don't make the
            // user wait. Just exit; their double-click was effectively a no-op.
            client.Connect(timeout: 1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("ACTIVATE");
        }
        catch (Exception ex)
        {
            CrashLogger.Info($"single-instance:client FAIL {ex.GetType().Name}: {ex.Message}");
            // Failure to signal is non-fatal \u2014 the second-launcher still exits,
            // the first instance just doesn't pop to the foreground automatically.
        }
    }
}
