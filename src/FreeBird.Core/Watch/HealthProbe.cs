using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace FreeBird.Core.Watch;

/// <summary>
/// Background ticker that runs alongside <see cref="WatchSupervisor.RunAsync"/>.
/// On each tick (every <see cref="WatchOptions.HealthProbeInterval"/>, default 5 minutes) it
/// inspects the supervisor's currently-managed tasks and:
/// <list type="bullet">
///   <item>Demotes any ACTIVE task whose watched directory has vanished
///         (via <see cref="WatchTask.DemoteIfDirectoryVanished"/> + <see cref="WatchTask.Cancel"/>
///         to wake the wrapped orchestrator out of its polling sleep).</item>
///   <item>Resurrects any DEAD task whose watched directory has reappeared
///         (via <see cref="WatchTask.TryResurrect"/>).</item>
/// </list>
///
/// Lifecycle: <see cref="RunAsync"/> is a long-running loop that exits cleanly when the supplied
/// <see cref="CancellationToken"/> is cancelled. It is intended to be started as a sibling Task
/// alongside the supervisor's RunAsync (T16 wires this into the CLI host).
///
/// Thread-safety: the probe relies on the supervisor's lock-guarded accessors
/// (<see cref="WatchSupervisor.GetActiveTasks"/>, <see cref="WatchSupervisor.GetDeadTasks"/>) which
/// each return a copy. <see cref="WatchTask.DemoteIfDirectoryVanished"/>, <see cref="WatchTask.Cancel"/>,
/// and <see cref="WatchTask.TryResurrect"/> are all individually thread-safe.
/// </summary>
public sealed class HealthProbe
{
    private readonly WatchSupervisor _supervisor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _log;
    private readonly TimeSpan _interval;

    public HealthProbe(
        WatchSupervisor supervisor,
        TimeProvider timeProvider,
        ILogger log,
        TimeSpan interval)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "HealthProbe interval must be > 0.");
        }
        _interval = interval;
    }

    /// <summary>
    /// Run the probe loop until <paramref name="ct"/> is cancelled. Each iteration sleeps for the
    /// configured interval (via <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
    /// so it is deterministic under <c>FakeTimeProvider</c>), then calls <see cref="TickOnce"/>.
    /// Cancellation exits cleanly without throwing.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _log.Information("health probe: started; interval={Interval}", _interval);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_interval, _timeProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                TickOnce();
            }
        }
        finally
        {
            _log.Information("health probe: stopped");
        }
    }

    /// <summary>
    /// Single iteration: demote vanished ACTIVE tasks (+ Cancel to wake the orchestrator),
    /// then attempt resurrection on every DEAD task. Internal so tests can drive it directly
    /// without spinning the <see cref="RunAsync"/> loop.
    /// </summary>
    internal void TickOnce()
    {
        // Demote any ACTIVE task whose watched directory has vanished. After demotion, signal
        // Cancel so the wrapped orchestrator (which may be parked in its polling sleep) wakes
        // up and exits. The supervisor's RunOneTaskIsolated treats task.IsDraining as a benign
        // OCE source so this does NOT produce a spurious WARN log.
        foreach (var task in _supervisor.GetActiveTasks())
        {
            if (task.DemoteIfDirectoryVanished())
            {
                task.Cancel();
            }
        }

        // Attempt to resurrect every DEAD task whose directory has reappeared. TryResurrect is
        // idempotent and returns false (no-op) when the directory is still missing.
        foreach (var task in _supervisor.GetDeadTasks())
        {
            task.TryResurrect();
        }
    }
}
