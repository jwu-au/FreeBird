using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using Serilog;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Writes Vorbis comments to FLAC files via the <c>metaflac</c> subprocess.
///
/// Why subprocess: TagLibSharp's FLAC tag-write path mutates the audio frames in
/// rare cases, breaking PCM-MD5 integrity checks. <c>metaflac --set-tag</c> is
/// proven-safe (it only rewrites the metadata block). See spec §6 Q6.
///
/// This class is NOT directly DI-bound as <see cref="ITagWriter"/> \u2014 the
/// <see cref="CompositeTagWriter"/> (T17) dispatches to it by file extension.
/// Registered as a concrete <c>SingleInstance</c> via the assembly scan.
/// </summary>
public sealed class FlacTagWriter : IFlacTagWriter, IDependency
{
    private const string MetaflacExe = "metaflac";

    private readonly IProcessRunner _runner;
    private readonly ILogger _log;

    public FlacTagWriter(IProcessRunner runner, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(log);
        _runner = runner;
        _log = log.ForContext<FlacTagWriter>();
    }

    /// <summary>
    /// Write tags to <paramref name="filePath"/>. Returns a closed-union result
    /// describing success or failure. Never throws for subprocess failures \u2014
    /// missing <c>metaflac</c>, non-zero exit, and I/O errors are all mapped to
    /// <see cref="TagWriteResult.Failed"/> with a distinct <c>SidecarReason</c>.
    /// </summary>
    public async Task<TagWriteResult> WriteAsync(string filePath, SongInfo song, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(song);

        var args = BuildArgs(filePath, song);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(MetaflacExe, args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // metaflac binary not on PATH \u2014 user installed FreeBird without flac.
            _log.Warning("metaflac not found on PATH; cannot write FLAC tags for {Path}", filePath);
            return new TagWriteResult.Failed("tag-tool-missing");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Unexpected error launching metaflac for {Path}", filePath);
            return new TagWriteResult.Failed("tag-io-error");
        }

        if (result.ExitCode != 0)
        {
            _log.Warning(
                "metaflac exit {Exit} writing tags to {Path}: {Stderr}",
                result.ExitCode, filePath, result.StandardError);
            return new TagWriteResult.Failed("tag-write-failed");
        }

        return TagWriteResult.Success.Instance;
    }

    /// <summary>
    /// Build the metaflac argument list. Always clears existing tags first
    /// (<c>--remove-all-tags</c>) to keep writes idempotent across re-runs;
    /// then sets ARTIST / TITLE / ALBUM. Multi-artist songs join with '/'
    /// (industry-standard for Vorbis ARTIST multi-value per spec Q4).
    ///
    /// The trailing path is the LAST argument, matching <c>metaflac</c>'s
    /// option-then-file calling convention.
    /// </summary>
    internal static IReadOnlyList<string> BuildArgs(string filePath, SongInfo song)
    {
        var args = new List<string>(capacity: 6)
        {
            "--remove-all-tags",
            "--set-tag=ARTIST=" + JoinArtists(song.Artists),
            "--set-tag=TITLE=" + song.Title,
        };

        if (!string.IsNullOrEmpty(song.Album))
        {
            args.Add("--set-tag=ALBUM=" + song.Album);
        }

        args.Add(filePath);
        return args;
    }

    private static string JoinArtists(IReadOnlyList<string> artists)
        => string.Join("/", artists);
}
