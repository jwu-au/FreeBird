using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Provisioning;
using Serilog;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Writes Vorbis comments to FLAC files via the <c>metaflac</c> subprocess.
///
/// Why subprocess: TagLibSharp's FLAC tag-write path mutates the audio frames in
/// rare cases, breaking PCM-MD5 integrity checks. <c>metaflac --set-tag</c> is
/// proven-safe (it only rewrites the metadata block). See spec §6 Q6.
///
/// The metaflac binary path is obtained via <see cref="IFlacBinaryResolver"/>;
/// if the resolver cannot locate a binary, a <see cref="FlacNotAvailableException"/>
/// is thrown. Callers (T13: CompositeTagWriter / FileProcessor) translate the
/// exception into pipeline policy (sidecar reason / WARN / hard fail).
///
/// This class is NOT directly DI-bound as <see cref="ITagWriter"/> \u2014 the
/// <see cref="CompositeTagWriter"/> (T17) dispatches to it by file extension.
/// Registered as a concrete <c>SingleInstance</c> via the assembly scan.
/// </summary>
public sealed class FlacTagWriter : IFlacTagWriter, IDependency
{
    private readonly IProcessRunner _runner;
    private readonly IFlacBinaryResolver _resolver;
    private readonly ILogger _log;

    public FlacTagWriter(IProcessRunner runner, IFlacBinaryResolver resolver, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(log);
        _runner = runner;
        _resolver = resolver;
        _log = log.ForContext<FlacTagWriter>();
    }

    /// <summary>
    /// Write tags to <paramref name="filePath"/>. Returns a closed-union result
    /// describing subprocess outcomes (success / non-zero exit / I/O error).
    /// Throws <see cref="FlacNotAvailableException"/> when the resolver cannot locate
    /// metaflac — callers decide whether to degrade to a sidecar or hard-fail.
    /// </summary>
    public async Task<TagWriteResult> WriteAsync(string filePath, SongInfo song, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(song);

        var metaflacResolution = await _resolver.ResolveMetaflacAsync(ct).ConfigureAwait(false);
        if (!metaflacResolution.IsAvailable)
        {
            throw new FlacNotAvailableException(
                "metaflac binary not available for FLAC tag write");
        }

        var args = BuildArgs(filePath, song);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(metaflacResolution.Path!, args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Defensive: resolver verified File.Exists, but the binary may have been
            // deleted between resolve and exec (race or AV quarantine on Windows).
            _log.Warning("metaflac failed to launch; cannot write FLAC tags for {Path}", filePath);
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
