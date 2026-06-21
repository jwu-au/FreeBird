using System.Threading;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Stateless gate that short-circuits a candidate source <c>.uc</c>/<c>.uc!</c>
/// when a per-source-stem JSON resolution marker under
/// <c>&lt;outputDir&gt;/.freebird-resolved/</c> confirms a prior resolution that
/// is still valid (fresh source size+mtime, matching naming template, referenced
/// output still on disk, and — for failure-status markers — the retry backoff
/// has not yet elapsed).
///
/// Depends only on primitives (<c>outputDir</c>, <c>namingTemplate</c>) plus an
/// injected <see cref="System.TimeProvider"/>, so BOTH <c>fb scan</c> (via
/// <c>ScanOrchestrator</c>) and <c>fb watch</c> (via <c>FilesystemSkipDecider</c>
/// Branch 3b) can consult the single implementation before any NetEase API call.
/// </summary>
public interface IResolvedMarkerGate : IDependency
{
    /// <summary>
    /// Returns a non-null <see cref="SkipDecision.Skip(SkipReason, string)"/>
    /// (<see cref="SkipReason.AlreadyDecodedViaMarker"/>) when ALL marker checks
    /// pass; otherwise <c>null</c> to fall through (caller proceeds to process or
    /// to further skip branches).
    /// </summary>
    SkipDecision? TryShortCircuit(
        string sourcePath,
        string outputDir,
        string namingTemplate,
        CancellationToken cancellationToken = default);
}
