using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Processes a single .uc / .uc! file end-to-end:
/// decrypt → sniff → integrity → atomic write or quarantine.
/// </summary>
public interface IFileProcessor : IDependency
{
    Task<ScanResult> ProcessAsync(
        string sourcePath,
        ScanOptions options,
        CancellationToken cancellationToken = default);
}
