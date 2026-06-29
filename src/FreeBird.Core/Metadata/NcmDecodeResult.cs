using FreeBird.Core.Models;

namespace FreeBird.Core.Metadata;

/// <summary>
/// Outcome of decoding a NetEase <c>.ncm</c> file via <c>INcmDecoder</c>. The
/// decrypted audio body is written to the caller-supplied output stream; this record
/// carries the side-band results.
/// </summary>
/// <param name="Metadata">Parsed song metadata from the embedded JSON block, or null when absent/unparseable.</param>
/// <param name="Cover">Embedded cover-art bytes (JPEG/PNG), or null when the file has no cover.</param>
/// <param name="Format">Audio format detected from the decrypted body (magic-byte sniff).</param>
public sealed record NcmDecodeResult(NcmMetadata? Metadata, byte[]? Cover, AudioFormat Format);
