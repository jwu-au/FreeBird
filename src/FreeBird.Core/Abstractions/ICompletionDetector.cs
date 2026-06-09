using System.Threading;
using System.Threading.Tasks;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Decides whether a candidate <c>.uc</c> / <c>.uc!</c> file is 'done downloading' (size has been
/// stable for N consecutive observations). Stateful: tracks history per file path.
/// Implementations must be thread-safe for concurrent observe-calls from different paths.
/// </summary>
public interface ICompletionDetector : IDependency
{
    /// <summary>
    /// Observe the current size/mtime of a file. Return true once size has remained
    /// unchanged for the configured number of consecutive observations (<paramref name="stabilityChecks"/>).
    /// Return false otherwise. Implementations must handle the path being deleted between
    /// calls (treat as 'no observation; clear any history').
    /// </summary>
    Task<bool> IsStableAsync(string filePath, int stabilityChecks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove tracked history for a path (call after successful processing or quarantine,
    /// to release memory and avoid stale entries).
    /// </summary>
    void Forget(string filePath);
}
