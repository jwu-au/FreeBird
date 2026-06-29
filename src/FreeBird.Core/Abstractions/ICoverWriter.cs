using FreeBird.Core.Metadata;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Embeds album-cover art into a decoded audio file. Dispatches per format
/// (FLAC → metaflac, MP3/M4A → TagLibSharp), mirroring <see cref="ITagWriter"/>.
///
/// This is a SEPARATE collaborator from <see cref="ITagWriter"/>: it is used only
/// by the NCM decode pipeline (which carries cover bytes alongside metadata). The
/// existing <c>.uc</c> tag path does not touch cover art and is unaffected.
/// </summary>
public interface ICoverWriter : IDependency
{
    /// <summary>
    /// Embed <paramref name="coverBytes"/> into the audio file at <paramref name="finalPath"/>.
    /// </summary>
    /// <param name="finalPath">Full path to the decoded music file (must already exist).</param>
    /// <param name="format">Sniffed audio format (determines which backend handles the write).</param>
    /// <param name="coverBytes">Raw JPEG or PNG image bytes (MIME auto-detected from magic).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating success or failure (with reason for sidecar logging).</returns>
    Task<TagWriteResult> WriteCoverAsync(string finalPath, AudioFormat format, byte[] coverBytes, CancellationToken ct);
}
