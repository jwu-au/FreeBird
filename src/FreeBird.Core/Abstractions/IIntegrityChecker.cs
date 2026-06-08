using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Verifies the integrity of a decrypted audio file.
/// Implementations may be L1 (structural), L3 (full PCM-MD5), or composite (routing by format/flag).
/// </summary>
public interface IIntegrityChecker : IDependency
{
    /// <summary>
    /// Checks the file at <paramref name="filePath"/> for the given <paramref name="format"/>.
    /// </summary>
    Task<IntegrityResult> CheckAsync(
        string filePath,
        AudioFormat format,
        CancellationToken cancellationToken = default);
}

/// <summary>L1-specific marker for explicit registration disambiguation.</summary>
public interface IL1IntegrityChecker : IIntegrityChecker { }

/// <summary>L3-specific marker for explicit registration disambiguation.</summary>
public interface IL3IntegrityChecker : IIntegrityChecker { }
