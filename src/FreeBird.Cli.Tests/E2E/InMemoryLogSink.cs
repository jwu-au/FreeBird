using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Serilog.Core;
using Serilog.Events;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// T18 — test sink that accumulates every Serilog <see cref="LogEvent"/> in a thread-safe
/// queue. Tests register it via <see cref="FreeBird.Cli.WatchRunner.AdditionalLogSinks"/>
/// before invoking the runner, then assert against <see cref="Events"/> after the run
/// has been cancelled. Used by <c>CycleLockE2ETests</c> to verify the debounced
/// WARN → DEBUG (4×) → WARN log sequence emitted by <c>WatchOrchestrator</c>.
/// </summary>
internal sealed class InMemoryLogSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    /// <summary>Snapshot of all captured events at the moment of access.</summary>
    public IReadOnlyList<LogEvent> Events => _events.ToArray();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}
