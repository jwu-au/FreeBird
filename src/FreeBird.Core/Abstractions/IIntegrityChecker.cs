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

/// <summary>
/// Composite routing integrity checker. Adds an overload that accepts an explicit
/// <see cref="IntegrityLevel"/> per call so the same instance can serve any CLI flag.
/// </summary>
public interface ICompositeIntegrityChecker : IIntegrityChecker
{
    /// <summary>
    /// Checks the file at <paramref name="filePath"/>, routing to L1 or L3 based on
    /// <paramref name="level"/>, <paramref name="format"/>, and runtime availability.
    /// </summary>
    Task<IntegrityResult> CheckAsync(
        string filePath,
        AudioFormat format,
        IntegrityLevel level,
        CancellationToken cancellationToken = default);
}
