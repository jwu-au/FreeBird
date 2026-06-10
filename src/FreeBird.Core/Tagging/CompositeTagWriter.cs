using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using Serilog;

namespace FreeBird.Core.Tagging;

/// <summary>
/// <see cref="ITagWriter"/> implementation that dispatches to a per-format
/// backend by <see cref="AudioFormat"/>. This is the type bound to
/// <see cref="ITagWriter"/> in the Autofac container.
///
/// Dispatch is driven by the format parameter \u2014 not by file extension \u2014
/// because the caller has already sniffed the format upstream (in
/// <c>FileProcessor</c>); re-deriving it from the extension would be a second
/// source of truth.
///
/// The three backends are exposed as internal interfaces
/// (<see cref="IFlacTagWriter"/> etc.) purely for test substitution \u2014
/// production code always resolves the concrete classes via DI.
/// </summary>
public sealed class CompositeTagWriter : ITagWriter
{
    private readonly IFlacTagWriter _flac;
    private readonly IMp3TagWriter _mp3;
    private readonly IM4aTagWriter _m4a;
    private readonly ILogger _log;

    public CompositeTagWriter(
        IFlacTagWriter flac,
        IMp3TagWriter mp3,
        IM4aTagWriter m4a,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(flac);
        ArgumentNullException.ThrowIfNull(mp3);
        ArgumentNullException.ThrowIfNull(m4a);
        ArgumentNullException.ThrowIfNull(log);
        _flac = flac;
        _mp3 = mp3;
        _m4a = m4a;
        _log = log.ForContext<CompositeTagWriter>();
    }

    /// <summary>
    /// Dispatches to the per-format backend. Throws
    /// <see cref="NotSupportedException"/> for <see cref="AudioFormat.Unknown"/>
    /// or any other unrecognized format \u2014 callers must sniff first.
    /// </summary>
    public Task<TagWriteResult> WriteAsync(
        string finalPath,
        AudioFormat format,
        SongInfo song,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(song);

        return format switch
        {
            AudioFormat.Flac => _flac.WriteAsync(finalPath, song, ct),
            AudioFormat.Mp3 => _mp3.WriteAsync(finalPath, song, ct),
            AudioFormat.M4a => _m4a.WriteAsync(finalPath, song, ct),
            _ => throw new NotSupportedException(
                $"Tag writing is not supported for audio format '{format}'."),
        };
    }
}
