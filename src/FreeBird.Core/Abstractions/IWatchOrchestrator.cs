using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// The watch-mode top-level orchestrator. Runs the initial full-sweep (unless
/// <see cref="WatchOptions.SkipInitialScan"/>), then enters the polling loop until cancellation.
/// Returns when cancellation is requested AND in-flight work is drained.
/// </summary>
public interface IWatchOrchestrator : IDependency
{
    /// <summary>
    /// Run the watch loop. Returns after graceful drain on cancellation,
    /// or immediately if <paramref name="hardAbortToken"/> is signaled.
    /// </summary>
    /// <param name="options">Watch configuration.</param>
    /// <param name="cancellationToken">Graceful cancellation. When fired, stop scheduling new files and drain in-flight (5s grace).</param>
    /// <param name="hardAbortToken">Hard abort. When fired, abandon in-flight work immediately.</param>
    /// <returns>Summary of work done across all cycles.</returns>
    Task<ScanSummary> RunAsync(
        WatchOptions options,
        CancellationToken cancellationToken = default,
        CancellationToken hardAbortToken = default);
}
