using System.Threading;
using System.Threading.Tasks;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Detects whether the official `flac` CLI is available on PATH (for L3 integrity).
/// </summary>
public interface IFlacProbe : IDependency
{
    /// <summary>
    /// Returns true if `flac --version` succeeded; false otherwise.
    /// Implementations should cache the result for the lifetime of the scope.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
