using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using Serilog;

namespace FreeBird.Core.Watch;

/// <summary>
/// Per-input-directory wrapper around an <see cref="IWatchOrchestrator"/> instance.
/// Owns the lifecycle state machine (Initializing → Active → Dead → Resurrecting → Active)
/// plus a sliding-window crash counter (3 crashes within 60s → DEAD).
///
/// Instances are constructed and managed by the multi-input watch supervisor (T08).
/// T06 (this class) covers only state ownership + factory-based (re)construction —
/// runtime start/stop/restart delegation is added in T07.
///
/// Thread-safety: all state mutations and reads go through <see cref="_stateLock"/>.
/// </summary>
public sealed class WatchTask
{
    /// <summary>Threshold of crashes within <see cref="CrashWindow"/> that forces DEAD.</summary>
    public const int CrashThreshold = 3;

    /// <summary>Sliding window over which crashes are counted.</summary>
    public static readonly TimeSpan CrashWindow = TimeSpan.FromSeconds(60);

    private readonly WatchInput _input;
    private readonly Func<IWatchOrchestrator> _orchestratorFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _log;

    // Rolling list of crash timestamps within the last CrashWindow.
    private readonly Queue<DateTimeOffset> _crashTimestamps = new();

    private readonly object _stateLock = new();
    private WatchTaskState _state;
    private IWatchOrchestrator? _currentOrchestrator;

    // Optional crash-injection seam, used only by tests via the 5-arg constructor.
    private readonly Func<Exception?>? _crashInjector;

    // Per-task CTS for shutdown signaling. Created on RunAsync entry and cleared on exit.
    // Read/written under _stateLock per T06 reviewer M3 (avoid captured-local TOCTOU).
    private CancellationTokenSource? _taskCts;

    /// <summary>The watch descriptor this task is bound to.</summary>
    public WatchInput Input => _input;

    /// <summary>Current lifecycle state. Thread-safe.</summary>
    public WatchTaskState State
    {
        get { lock (_stateLock) { return _state; } }
    }

    /// <summary>For T07/T11 to access the current orchestrator instance (null when DEAD).</summary>
    internal IWatchOrchestrator? CurrentOrchestrator
    {
        get { lock (_stateLock) { return _currentOrchestrator; } }
    }

    public WatchTask(
        WatchInput input,
        Func<IWatchOrchestrator> orchestratorFactory,
        TimeProvider timeProvider,
        ILogger log)
        : this(input, orchestratorFactory, timeProvider, log, crashInjector: null)
    {
    }

    /// <summary>
    /// Test-only constructor overload that adds a crash-injection seam invoked at the top of
    /// <see cref="RunAsync"/> (before delegating to the wrapped orchestrator). Production code
    /// should use the 4-argument constructor.
    /// </summary>
    public WatchTask(
        WatchInput input,
        Func<IWatchOrchestrator> orchestratorFactory,
        TimeProvider timeProvider,
        ILogger log,
        Func<Exception?>? crashInjector)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _orchestratorFactory = orchestratorFactory ?? throw new ArgumentNullException(nameof(orchestratorFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _crashInjector = crashInjector;

        // Initial state: validate directory NOW (born-DEAD detection).
        if (!Directory.Exists(_input.Path))
        {
            _state = WatchTaskState.Dead;
            _log.Warning(
                "[watch={Base}] born DEAD: directory does not exist at {Path}",
                _input.BaseName, _input.Path);
            return;
        }

        _state = WatchTaskState.Initializing;
        try
        {
            _currentOrchestrator = _orchestratorFactory();
            _state = WatchTaskState.Active;
        }
        catch (Exception ex)
        {
            _state = WatchTaskState.Dead;
            _currentOrchestrator = null;
            _log.Error(ex, "[watch={Base}] born DEAD: orchestrator factory threw", _input.BaseName);
        }
    }

    /// <summary>
    /// Record a crash. Returns <c>true</c> if the task transitioned to DEAD
    /// (i.e. <see cref="CrashThreshold"/> crashes occurred within <see cref="CrashWindow"/>).
    /// Thread-safe.
    /// </summary>
    public bool RecordCrash(Exception ex)
    {
        lock (_stateLock)
        {
            var now = _timeProvider.GetUtcNow();

            // Evict crashes outside the window.
            while (_crashTimestamps.Count > 0 && now - _crashTimestamps.Peek() > CrashWindow)
            {
                _crashTimestamps.Dequeue();
            }
            _crashTimestamps.Enqueue(now);

            if (_crashTimestamps.Count >= CrashThreshold)
            {
                _state = WatchTaskState.Dead;
                _currentOrchestrator = null;
                _log.Error(
                    ex,
                    "[watch={Base}] crashed {Count} times in {Window}s; entering DEAD",
                    _input.BaseName, _crashTimestamps.Count, CrashWindow.TotalSeconds);
                return true;
            }

            _log.Warning(
                ex,
                "[watch={Base}] crash {Count}/{Threshold} in {Window}s; will retry",
                _input.BaseName, _crashTimestamps.Count, CrashThreshold, CrashWindow.TotalSeconds);
            return false;
        }
    }

    /// <summary>
    /// Attempt to resurrect from DEAD. Called by the health probe (T11) when it sees the
    /// directory reappear. Returns <c>true</c> if successfully transitioned to ACTIVE.
    ///
    /// Idempotent: returns false if not currently DEAD, or if the directory is still missing,
    /// or if the orchestrator factory throws.
    /// </summary>
    public bool TryResurrect()
    {
        lock (_stateLock)
        {
            if (_state != WatchTaskState.Dead)
            {
                return false;
            }
            if (!Directory.Exists(_input.Path))
            {
                return false;
            }

            _state = WatchTaskState.Resurrecting;
            _log.Information("[watch={Base}] resurrecting: directory present", _input.BaseName);

            try
            {
                _currentOrchestrator = _orchestratorFactory();
                _state = WatchTaskState.Active;
                _crashTimestamps.Clear();
                _log.Information("[watch={Base}] resurrected to ACTIVE", _input.BaseName);
                return true;
            }
            catch (Exception ex)
            {
                _state = WatchTaskState.Dead;
                _currentOrchestrator = null;
                _log.Error(ex, "[watch={Base}] resurrection failed: orchestrator factory threw", _input.BaseName);
                return false;
            }
        }
    }

    /// <summary>
    /// Called by the health probe (T11) to demote ACTIVE → DEAD when the watched directory
    /// has vanished. Returns <c>true</c> if state changed.
    /// </summary>
    public bool DemoteIfDirectoryVanished()
    {
        lock (_stateLock)
        {
            if (_state != WatchTaskState.Active)
            {
                return false;
            }
            if (Directory.Exists(_input.Path))
            {
                return false;
            }

            _state = WatchTaskState.Dead;
            _currentOrchestrator = null;
            _log.Warning("[watch={Base}] directory vanished; demoting to DEAD", _input.BaseName);
            return true;
        }
    }

    /// <summary>
    /// Run the wrapped orchestrator's polling loop until cancelled, errored, or DEAD.
    /// Delegates to <see cref="IWatchOrchestrator.RunAsync"/> using a linked CTS so that
    /// either the caller's cancellation or an internal <see cref="Cancel"/> propagates.
    ///
    /// Behavior:
    /// - DEAD task → returns empty <see cref="ScanSummary"/> without invoking the orchestrator.
    /// - Cancellation → propagates <see cref="OperationCanceledException"/> to caller.
    /// - Non-fatal crash → calls <see cref="RecordCrash"/> and rethrows so the supervisor can restart.
    /// - Fatal crash (threshold reached) → calls <see cref="RecordCrash"/>, transitions to DEAD,
    ///   and returns an empty <see cref="ScanSummary"/> (graceful exit; supervisor sees DEAD on next probe).
    /// </summary>
    /// <summary>
    /// Run the wrapped orchestrator's polling loop until cancelled, errored, or DEAD.
    /// Caller contract: NOT reentrant — a single WatchTask supports one concurrent run.
    /// The supervisor (T09) is responsible for serializing per-task RunAsync calls.
    /// Returns empty ScanSummary if task is born-DEAD or transitions to DEAD via crash threshold.
    /// </summary>
    public async Task<ScanSummary> RunAsync(WatchOptions options, CancellationToken externalCt)
    {
        if (options is null) { throw new ArgumentNullException(nameof(options)); }

        // Born-DEAD: return empty summary immediately, don't try to construct anything.
        if (State == WatchTaskState.Dead)
        {
            _log.Warning("[watch={Base}] RunAsync called on DEAD task; returning empty summary", _input.BaseName);
            return EmptySummary();
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);

        IWatchOrchestrator? orchestrator;
        lock (_stateLock)
        {
            _taskCts = linkedCts;
            orchestrator = _currentOrchestrator;
        }

        if (orchestrator is null)
        {
            // Race: state went DEAD between top-of-method check and lock acquisition.
            lock (_stateLock) { _taskCts = null; }
            _log.Warning("[watch={Base}] orchestrator unavailable at RunAsync entry", _input.BaseName);
            return EmptySummary();
        }

        try
        {
            // Test-only crash injection BEFORE delegating.
            var injected = _crashInjector?.Invoke();
            if (injected is not null) { throw injected; }

            return await orchestrator.RunAsync(options, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            // Expected cancellation — propagate.
            throw;
        }
        catch (Exception ex)
        {
            // Genuine crash — record + maybe transition to DEAD.
            bool wentDead = RecordCrash(ex);
            if (wentDead)
            {
                // Threshold reached. RecordCrash already logged at Error. Graceful exit
                // so the supervisor sees DEAD on its next probe instead of an exception.
                return EmptySummary();
            }
            // Non-fatal crash: rethrow so the supervisor can decide to restart.
            throw;
        }
        finally
        {
            lock (_stateLock) { _taskCts = null; }
        }
    }

    /// <summary>
    /// Signal this task to cancel its current <see cref="RunAsync"/>. Used by the supervisor
    /// for shutdown or probe-driven demotion. No-op if no run is in flight.
    /// </summary>
    /// <summary>
    /// Signal this task to cancel its current RunAsync (used by supervisor for shutdown / probe demote).
    /// Thread-safe and idempotent. Tolerant of the narrow dispose race between snapshot and cancel
    /// (reviewer M4): if the in-flight RunAsync's finally has cleared and disposed the linked CTS
    /// between our snapshot and our Cancel call, we swallow ObjectDisposedException as a no-op.
    /// </summary>
    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock (_stateLock) { cts = _taskCts; }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { /* M4: harmless dispose race */ }
    }

    private static ScanSummary EmptySummary() => new ScanSummary(
        Processed: 0,
        Ok: 0,
        Skipped: 0,
        UnknownFormat: 0,
        IntegrityFailed: 0,
        Errors: 0,
        Duration: TimeSpan.Zero);
}
