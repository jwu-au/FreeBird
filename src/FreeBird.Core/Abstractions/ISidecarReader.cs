using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Reads and parses a quarantine sidecar <c>.txt</c> file written by <c>FileProcessor</c>.
/// Tolerant of v1 (5-field) and v2 (7-field) formats; unknown fields are preserved.
/// </summary>
public interface ISidecarReader : IDependency
{
    /// <summary>
    /// Try to read the sidecar at the given path. Returns null if missing, unreadable,
    /// or fundamentally malformed (no <c>timestamp:</c> line). Tolerates missing optional
    /// fields (<c>source_size</c>, <c>source_mtime</c>) — these end up null on the returned
    /// <see cref="SidecarRecord"/>.
    /// </summary>
    Task<SidecarRecord?> TryReadAsync(string sidecarPath, CancellationToken cancellationToken = default);
}
