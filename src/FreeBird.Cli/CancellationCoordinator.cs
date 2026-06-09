using System;
using System.Threading;
using Serilog;

namespace FreeBird.Cli;

/// <summary>
/// T12 — coordinates the two-phase shutdown initiated by Ctrl-C / SIGTERM in the watch loop.
///
/// State machine:
///   * Press Ctrl-C once  →  cancel the <see cref="Graceful"/> token + arm a grace timer.
///                           In-flight files get up to the grace period to finish cleanly.
///   * Grace timer fires  →  cancel the <see cref="Hard"/> token (forced abort).
///   * Press Ctrl-C twice →  cancel <see cref="Hard"/> immediately (force abort now,
///                           bypassing the remaining grace window).
///   * Any further Ctrl-C →  silently counted (so the runner can still return exit code 130)
///                           but no additional action.
///   * SIGTERM            →  treated as immediate hard abort (services convention: SIGTERM is
///                           not graceful). Only the hard token is cancelled — graceful is
///                           left alone because the orchestrator's parallel loop watches the
///                           hard token anyway.
///
/// This type does <b>not</b> subscribe to OS signals — that's a CLI/host concern wired up by
/// <see cref="WatchRunner"/>. Keeping the state machine isolated lets us unit-test it with a
/// <see cref="TimeProvider"/> double (FakeTimeProvider).
/// </summary>
public sealed class CancellationCoordinator : IDisposable
{
    /// <summary>
    /// Default grace period between first Ctrl-C (graceful) and forced hard abort.
    /// </summary>
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _graceful = new();
    private readonly CancellationTokenSource _hard = new();
    private readonly CancellationToken _gracefulToken;
    private readonly CancellationToken _hardToken;
    private readonly TimeSpan _gracePeriod;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private int _signalCount;
    private ITimer? _graceTimer;
    private bool _disposed;

    public CancellationCoordinator(TimeProvider timeProvider, ILogger logger, TimeSpan? gracePeriod = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _timeProvider = timeProvider;
        _logger = logger;
        _gracePeriod = gracePeriod ?? DefaultGracePeriod;
        // Cache tokens at construction. CancellationToken is a struct and remains valid (carrying
        // any cancelled state) even after the underlying CTS has been disposed — so callers can
        // still observe IsCancellationRequested post-Dispose.
        _gracefulToken = _graceful.Token;
        _hardToken = _hard.Token;
    }

    /// <summary>
    /// Cancelled on the first Ctrl-C. The orchestrator should observe this as "stop accepting
    /// new work; let in-flight items finish."
    /// </summary>
    public CancellationToken Graceful => _gracefulToken;

    /// <summary>
    /// Cancelled when either (a) the grace timer expires, (b) a second Ctrl-C arrives, or
    /// (c) a SIGTERM arrives. Forces in-flight work to abort.
    /// </summary>
    public CancellationToken Hard => _hardToken;

    /// <summary>
    /// Total number of shutdown signals received. Used by <see cref="WatchRunner"/> to map to
    /// the Unix convention exit code 130 when SIGINT was involved.
    /// </summary>
    public int SignalCount => Volatile.Read(ref _signalCount);

    /// <summary>
    /// Invoked from the Console.CancelKeyPress handler. Implements the double-Ctrl-C escalation.
    /// </summary>
    public void OnCancelRequested()
    {
        var count = Interlocked.Increment(ref _signalCount);
        if (count == 1)
        {
            _logger.Information(
                "Graceful shutdown requested. In-flight work will complete within {GracePeriod}. Press Ctrl-C again to force abort.",
                _gracePeriod);
            SafeCancel(_graceful);

            // Arm the grace timer. Use TimeProvider so tests can advance the clock.
            lock (_gate)
            {
                if (!_disposed)
                {
                    _graceTimer = _timeProvider.CreateTimer(
                        _ => OnGraceExpired(),
                        state: null,
                        dueTime: _gracePeriod,
                        period: Timeout.InfiniteTimeSpan);
                }
            }
        }
        else if (count == 2)
        {
            _logger.Warning("Forced abort requested (Ctrl-C pressed twice).");
            DisposeTimerLocked();
            SafeCancel(_hard);
        }
        // count >= 3: silently absorbed; we're already aborting.
    }

    /// <summary>
    /// Invoked from a PosixSignal SIGTERM handler. Skips the graceful path entirely.
    /// </summary>
    public void OnHardSignalRequested()
    {
        Interlocked.Increment(ref _signalCount);
        _logger.Warning("Hard signal received (SIGTERM). Forcing abort.");
        DisposeTimerLocked();
        SafeCancel(_hard);
    }

    private void OnGraceExpired()
    {
        if (!_hard.IsCancellationRequested)
        {
            _logger.Warning("Grace period expired. Forcing abort of in-flight work.");
            SafeCancel(_hard);
        }
    }

    private void DisposeTimerLocked()
    {
        lock (_gate)
        {
            _graceTimer?.Dispose();
            _graceTimer = null;
        }
    }

    private void SafeCancel(CancellationTokenSource cts)
    {
        // Cancel() can throw AggregateException if a registered callback throws. We intentionally
        // swallow here because the coordinator must never propagate exceptions back into the OS
        // signal handler thread.
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Benign: CTS was already disposed (e.g. coordinator Dispose() raced with a signal).
        }
        catch (Exception ex)
        {
            // A registered cancellation callback threw. We can't propagate — we're on a signal
            // handler thread — but at least log it so users have a debugging breadcrumb.
            _logger.Warning(ex, "Suppressed exception while cancelling token");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _graceTimer?.Dispose();
            _graceTimer = null;
        }
        _graceful.Dispose();
        _hard.Dispose();
    }
}
