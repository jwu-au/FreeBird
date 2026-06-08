using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Top-level scan driver: enumerate .uc/.uc! files, dispatch them through IFileProcessor
/// with Parallel.ForEachAsync, aggregate into ScanSummary.
/// </summary>
public interface IScanOrchestrator : IDependency
{
    Task<ScanSummary> RunAsync(ScanOptions options, CancellationToken cancellationToken = default);
}
