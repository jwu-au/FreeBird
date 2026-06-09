using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Given a candidate source file and the watch options, decide whether to process or skip.
/// Inspects output dir for existing decoded file (Q3-B: skip if exists), inspects
/// <c>.freebird-failed/</c> sidecars for <c>source_size</c>/<c>source_mtime</c> match
/// (Q2-B: skip if source unchanged since last failure).
/// </summary>
public interface ISkipDecider : IDependency
{
    Task<SkipDecision> DecideAsync(
        string sourcePath,
        WatchOptions options,
        CancellationToken cancellationToken = default);
}
