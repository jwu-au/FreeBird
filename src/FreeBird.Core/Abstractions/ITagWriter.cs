using FreeBird.Core.Metadata;
using FreeBird.Core.Models;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Writes metadata tags into a decoded audio file. Dispatches per format
/// (FLAC → metaflac, MP3/M4A → TagLibSharp). Invoked AFTER the integrity check and
/// AFTER atomic rename to the final filename.
/// </summary>
public interface ITagWriter : IDependency
{
    /// <summary>
    /// Write tags into the file at <paramref name="finalPath"/>.
    /// </summary>
    /// <param name="finalPath">Full path to the decoded music file (must already exist).</param>
    /// <param name="format">Sniffed audio format (determines which backend handles the write).</param>
    /// <param name="song">Metadata to write. Title, Artists list, Album.</param>
    /// <param name="ct">Cancellation token (best-effort for subprocess; immediate for in-process libs).</param>
    /// <returns>Result indicating success, failure (with reason for sidecar logging), or skip.</returns>
    Task<TagWriteResult> WriteAsync(string finalPath, AudioFormat format, SongInfo song, CancellationToken ct);
}
