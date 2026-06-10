using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using Serilog;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Writes MP4-atom tags to M4A files via TagLibSharp.
///
/// M4A uses MP4 metadata atoms (not ID3) \u2014 no extra default configuration
/// is needed beyond what TagLibSharp ships with. Code path is otherwise
/// identical to <see cref="Mp3TagWriter"/>; format auto-detection inside
/// <c>TagLib.File.Create</c> picks the right backend.
///
/// This class is NOT directly DI-bound as <see cref="ITagWriter"/> \u2014 the
/// <see cref="CompositeTagWriter"/> (T17) dispatches to it by audio format.
/// </summary>
public sealed class M4aTagWriter : IM4aTagWriter, IDependency
{
    private readonly ILogger _log;

    public M4aTagWriter(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<M4aTagWriter>();
    }

    /// <summary>
    /// Write tags to <paramref name="filePath"/>. Returns a closed-union result.
    /// Never throws for TagLib errors. Cancellation surfaces as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public Task<TagWriteResult> WriteAsync(string filePath, SongInfo song, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(song);
        ct.ThrowIfCancellationRequested();

        try
        {
            using var file = TagLib.File.Create(filePath);
            file.Tag.Performers = song.Artists.ToArray();
            file.Tag.Title = song.Title;
            file.Tag.Album = song.Album;
            file.Save();
            return Task.FromResult<TagWriteResult>(TagWriteResult.Success.Instance);
        }
        catch (TagLib.CorruptFileException ex)
        {
            _log.Warning(ex, "Corrupt M4A file while writing tags: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("tag-corrupt-file"));
        }
        catch (TagLib.UnsupportedFormatException ex)
        {
            _log.Warning(ex, "Unsupported M4A format while writing tags: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("tag-unsupported"));
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "I/O error writing M4A tags: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("tag-io-error"));
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warning(ex, "Access denied writing M4A tags: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("tag-io-error"));
        }
    }
}
