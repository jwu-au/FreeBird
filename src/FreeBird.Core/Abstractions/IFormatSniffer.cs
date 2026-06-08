using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Detects audio format by reading magic bytes from a file.
/// </summary>
public interface IFormatSniffer : IDependency
{
    /// <summary>
    /// Peeks the first ~12 bytes of the given file and returns the detected format.
    /// Returns <see cref="AudioFormat.Unknown"/> if no known signature matches.
    /// </summary>
    Task<AudioFormat> SniffAsync(string filePath, CancellationToken cancellationToken = default);
}
