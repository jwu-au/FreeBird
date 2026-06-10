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
/// Writes ID3v2.4 (UTF-8) tags to MP3 files via TagLibSharp.
///
/// Why TagLibSharp here (and not for FLAC): TagLib's MP3 path only mutates the
/// ID3 frames, leaving the audio data and side-info untouched. That's safe for
/// integrity checks (no PCM-MD5 concern \u2014 MP3 has no built-in audio hash).
///
/// This class is NOT directly DI-bound as <see cref="ITagWriter"/> \u2014 the
/// <see cref="CompositeTagWriter"/> (T17) dispatches to it by audio format.
/// </summary>
public sealed class Mp3TagWriter : IMp3TagWriter, IDependency
{
    /// <summary>
    /// Static idempotent ID3v2.4 + UTF-8 default. Applies process-wide \u2014 also
    /// affects M4A (TagLib uses these defaults for any ID3v2 it touches), but in
    /// FreeBird TagLib only writes ID3v2 inside this class, so the broader reach
    /// is a non-issue. Set once at type initialization; re-assignment would be
    /// safe (same values) but pointless.
    /// </summary>
    static Mp3TagWriter()
    {
        TagLib.Id3v2.Tag.DefaultVersion = 4;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
        TagLib.Id3v2.Tag.DefaultEncoding = TagLib.StringType.UTF8;
    }

    private readonly ILogger _log;

    public Mp3TagWriter(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<Mp3TagWriter>();
    }

    /// <summary>
    /// Write tags to <paramref name="filePath"/>. Returns a closed-union result.
    /// Never throws for TagLib errors \u2014 corrupt / unsupported / I/O failures
    /// are mapped to <see cref="TagWriteResult.Failed"/> with a sidecar reason
    /// code. Cancellation surfaces as <see cref="OperationCanceledException"/>.
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
            _log.Warning(ex, "Corrupt MP3 file while writing tags: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("tag-corrupt-file"));
        }
        catch (TagLib.UnsupportedFormatException ex)
        {
            _log.Warning(ex, "Unsupported MP3 format while writing tags: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("tag-unsupported"));
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "I/O error writing MP3 tags: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("tag-io-error"));
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warning(ex, "Access denied writing MP3 tags: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("tag-io-error"));
        }
    }
}
