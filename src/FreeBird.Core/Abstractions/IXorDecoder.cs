using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Streaming XOR decoder for Netease cache files (key = 0xA3).
/// </summary>
public interface IXorDecoder : IDependency
{
    /// <summary>
    /// Reads from input stream, XORs each byte with 0xA3, writes to output stream.
    /// Processes in 64KB chunks; honors cancellation.
    /// </summary>
    Task DecodeAsync(Stream input, Stream output, CancellationToken cancellationToken = default);
}
