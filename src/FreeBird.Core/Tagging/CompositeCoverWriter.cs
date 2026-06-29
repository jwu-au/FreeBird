using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using Serilog;

namespace FreeBird.Core.Tagging;

/// <summary>
/// <see cref="ICoverWriter"/> implementation that dispatches to a per-format
/// backend by <see cref="AudioFormat"/>. This is the type bound to
/// <see cref="ICoverWriter"/> in the Autofac container.
///
/// Dispatch is driven by the format parameter — not the file extension — because
/// the caller (NcmFileProcessor) has already sniffed the format upstream.
///
/// MP3 and M4A share a single TagLib backend (<see cref="IMp3M4aCoverWriter"/>);
/// FLAC uses the metaflac backend (<see cref="IFlacCoverWriter"/>) to preserve
/// PCM-MD5. The two backends are exposed as interfaces purely for test
/// substitution; production resolves the concrete classes via DI.
/// </summary>
public sealed class CompositeCoverWriter : ICoverWriter
{
    private readonly IFlacCoverWriter _flac;
    private readonly IMp3M4aCoverWriter _taglib;
    private readonly ILogger _log;

    public CompositeCoverWriter(
        IFlacCoverWriter flac,
        IMp3M4aCoverWriter taglib,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(flac);
        ArgumentNullException.ThrowIfNull(taglib);
        ArgumentNullException.ThrowIfNull(log);
        _flac = flac;
        _taglib = taglib;
        _log = log.ForContext<CompositeCoverWriter>();
    }

    /// <summary>
    /// Dispatches to the per-format backend. Throws
    /// <see cref="NotSupportedException"/> for <see cref="AudioFormat.Unknown"/>
    /// or any other unrecognized format — callers must sniff first. This matches
    /// <see cref="CompositeTagWriter"/>'s behaviour exactly.
    /// </summary>
    public Task<TagWriteResult> WriteCoverAsync(
        string finalPath,
        AudioFormat format,
        byte[] coverBytes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(coverBytes);

        return format switch
        {
            AudioFormat.Flac => _flac.WriteCoverAsync(finalPath, coverBytes, ct),
            AudioFormat.Mp3 => _taglib.WriteCoverAsync(finalPath, coverBytes, ct),
            AudioFormat.M4a => _taglib.WriteCoverAsync(finalPath, coverBytes, ct),
            _ => throw new NotSupportedException(
                $"Cover writing is not supported for audio format '{format}'."),
        };
    }
}
