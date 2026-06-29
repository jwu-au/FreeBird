using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Decodes a NetEase Cloud Music <c>.ncm</c> file: validates the magic header,
/// AES-decrypts the RC4 key + metadata, writes the decrypted audio body to
/// <paramref name="output"/>, and returns parsed metadata, cover art, and the
/// detected audio format. Implementation arrives in later tasks; failure modes are
/// surfaced via <see cref="NcmDecodeException"/>.
/// </summary>
public interface INcmDecoder : IDependency
{
    /// <summary>
    /// Reads the .ncm file at <paramref name="sourcePath"/>, writes the decrypted
    /// audio body to <paramref name="output"/>, and returns the decode result.
    /// Throws <see cref="NcmDecodeException"/> on bad magic / corrupt header /
    /// AES failure / truncated input.
    /// </summary>
    Task<NcmDecodeResult> DecodeAsync(string sourcePath, Stream output, CancellationToken ct = default);
}
