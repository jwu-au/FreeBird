using System;
using System.Collections.Generic;
using System.IO;
using FreeBird.Core.Abstractions;
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
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _orchestratorFactory = orchestratorFactory ?? throw new ArgumentNullException(nameof(orchestratorFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));

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
}
