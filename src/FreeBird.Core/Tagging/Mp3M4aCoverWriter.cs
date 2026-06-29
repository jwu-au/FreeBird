using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using Serilog;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Embeds cover art into MP3 and M4A files via TagLibSharp. A single class handles
/// both because <c>TagLib.File.Create</c> auto-detects the container and exposes a
/// uniform <c>Tag.Pictures</c> API (ID3v2 APIC for MP3, <c>covr</c> atom for M4A).
///
/// Why TagLibSharp here (and not for FLAC): TagLib's MP3/M4A path only mutates the
/// tag region, leaving audio data untouched — safe for integrity (neither format
/// carries a built-in audio hash like FLAC's PCM-MD5).
///
/// Used only by the NCM pipeline (via <see cref="CompositeCoverWriter"/>); the
/// existing <c>.uc</c> tag path is unaffected.
/// </summary>
public sealed class Mp3M4aCoverWriter : IMp3M4aCoverWriter, IDependency
{
    private readonly ILogger _log;

    public Mp3M4aCoverWriter(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<Mp3M4aCoverWriter>();
    }

    /// <summary>
    /// Embed <paramref name="coverBytes"/> as the single front-cover picture.
    /// Replaces any existing pictures (idempotent re-runs yield exactly one).
    /// Never throws for TagLib errors — they map to <see cref="TagWriteResult.Failed"/>.
    /// </summary>
    public Task<TagWriteResult> WriteCoverAsync(string filePath, byte[] coverBytes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(coverBytes);
        ct.ThrowIfCancellationRequested();

        try
        {
            using var file = TagLib.File.Create(filePath);
            var picture = new TagLib.Picture(new TagLib.ByteVector(coverBytes))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = CoverMime.Detect(coverBytes),
            };
            file.Tag.Pictures = new TagLib.IPicture[] { picture };
            file.Save();
            return Task.FromResult<TagWriteResult>(TagWriteResult.Success.Instance);
        }
        catch (TagLib.CorruptFileException ex)
        {
            _log.Warning(ex, "Corrupt file while writing cover: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("cover-corrupt-file"));
        }
        catch (TagLib.UnsupportedFormatException ex)
        {
            _log.Warning(ex, "Unsupported format while writing cover: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("cover-unsupported"));
        }
        catch (IOException ex)
        {
            _log.Warning(ex, "I/O error writing cover: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("cover-io-error"));
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warning(ex, "Access denied writing cover: {Path}", filePath);
            return Task.FromResult<TagWriteResult>(new TagWriteResult.Failed("cover-io-error"));
        }
    }
}
