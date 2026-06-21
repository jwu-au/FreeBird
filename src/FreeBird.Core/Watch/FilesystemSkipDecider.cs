using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Models;
using FreeBird.Core.Naming;
using Serilog;

namespace FreeBird.Core.Watch;

/// <summary>
/// The v2 <see cref="ISkipDecider"/>. Decides whether a candidate source <c>.uc</c>/<c>.uc!</c>
/// should be processed this watch cycle, based on:
/// <list type="number">
///   <item><description><see cref="SkipReason.SourceTooSmall"/> — source is below <see cref="WatchOptions.MinFileSizeBytes"/>.</description></item>
///   <item><description><see cref="SkipReason.AlreadyDecoded"/> — a successful output (<c>.mp3</c>/<c>.flac</c>/<c>.m4a</c>) exists in <see cref="WatchOptions.OutputDir"/>.</description></item>
///   <item><description><see cref="SkipReason.AlreadyDecodedViaMarker"/> — a per-source-stem JSON marker under <c>&lt;OutputDir&gt;/.freebird-resolved/</c> confirms a prior successful resolution and is still fresh (v3.0.1, T05).</description></item>
///   <item><description><see cref="SkipReason.SourceUnchangedSinceFailure"/> — a sidecar under <c>&lt;OutputDir&gt;/.freebird-failed/</c> records the same (size, mtime) as the source today.</description></item>
/// </list>
/// First match wins. <see cref="SkipReason.NotYetStable"/> is out of scope; the watch loop calls
/// <see cref="ICompletionDetector"/> separately before invoking this decider.
/// </summary>
public sealed class FilesystemSkipDecider : ISkipDecider
{
    private static readonly string[] OutputExtensions = { ".mp3", ".flac", ".m4a" };
    private const string FailedDirName = ".freebird-failed";

    private readonly ISidecarReader _sidecarReader;
    private readonly ILogger _logger;
    private readonly IResolvedMarkerGate _resolvedMarkerGate;

    public FilesystemSkipDecider(
        ISidecarReader sidecarReader,
        ILogger logger,
        IResolvedMarkerGate resolvedMarkerGate)
    {
        _sidecarReader = sidecarReader ?? throw new ArgumentNullException(nameof(sidecarReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resolvedMarkerGate = resolvedMarkerGate ?? throw new ArgumentNullException(nameof(resolvedMarkerGate));
    }

    public async Task<SkipDecision> DecideAsync(
        string sourcePath,
        WatchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        // Defensive: stat the source. If it's missing or unreadable, hand off to Process
        // (caller / FileProcessor will deal with it). NotYetStable is not our concern.
        FileInfo info;
        try
        {
            info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                return SkipDecision.Process();
            }
        }
        catch (IOException)
        {
            return SkipDecision.Process();
        }
        catch (UnauthorizedAccessException)
        {
            return SkipDecision.Process();
        }

        // 2. SourceTooSmall
        if (info.Length < options.MinFileSizeBytes)
        {
            var decision = SkipDecision.Skip(
                SkipReason.SourceTooSmall,
                $"size={info.Length} < min={options.MinFileSizeBytes}");
            _logger.Debug("Skip decision: {Path} -> {Reason}", sourcePath, decision.Reason);
            return decision;
        }

        var stem = StemBasedFileNamer.GetStem(sourcePath);

        // Branch 3a — Stem-equals-output exact match (legacy v2 / external callers
        // that pre-populate stem-named outputs). First existing successful output wins.
        foreach (var ext in OutputExtensions)
        {
            var candidate = Path.Combine(options.OutputDir, stem + ext);
            if (File.Exists(candidate))
            {
                var decision = SkipDecision.Skip(SkipReason.AlreadyDecoded, $"exists: {candidate}");
                _logger.Debug("Skip decision: {Path} -> {Reason}", sourcePath, decision.Reason);
                return decision;
            }
        }

        // Branch 3b — Per-source-stem JSON marker short-circuit (v3.0.1, T05).
        //
        // After a successful resolution the FileProcessor writes a JSON marker at
        // "<outputDir>/.freebird-resolved/<sourceStem>.json" recording the source's
        // (size, mtime), the naming template that produced the output, and the final
        // output filename. We treat the marker as a freshness-keyed proof: if all
        // freshness/template/retry checks pass AND the referenced output still exists,
        // we Skip without consulting Branch 3c — silencing the per-poll INF log spam
        // that drove this fix (D1 in the design spec).
        //
        // The marker write is advisory (D2 in the design spec); a missing/malformed
        // marker simply falls through. The serializer's TryRead is lenient — it
        // returns false on missing/parse error and logs WRN on parse error itself,
        // so we never re-log here.
        var markerDecision = _resolvedMarkerGate.TryShortCircuit(
            sourcePath, options.OutputDir, options.NamingTemplate, cancellationToken);
        if (markerDecision is not null)
        {
            return markerDecision;
        }

        // Branch 3c — MusicId-only output fallback (spec §10 / OA2).
        //
        // v3 produces "{musicId}.{ext}" on metadata-resolution fallback. For sources
        // whose stem starts with the musicId (canonical NetEase cache naming, including
        // composite "<id>-<bitrate>.uc"), an existing "{musicId}.{ext}" indicates a
        // prior run hit the fallback. Behavior:
        //   * --offline=true → current run WILL produce {musicId}.{ext}, so Skip.
        //   * --offline=false → bias toward Process so the current run can resolve
        //     metadata and produce the canonical filename. Logged at DBG (v3.0.1)
        //     because Branch 3b will short-circuit subsequent polls once the marker
        //     is written.
        var musicIdStem = MusicIdExtractor.TryExtractAsString(sourcePath);
        if (musicIdStem is not null && !string.Equals(musicIdStem, stem, StringComparison.Ordinal))
        {
            foreach (var ext in OutputExtensions)
            {
                var musicIdCandidate = Path.Combine(options.OutputDir, musicIdStem + ext);
                if (File.Exists(musicIdCandidate))
                {
                    if (options.Offline)
                    {
                        var decision = SkipDecision.Skip(SkipReason.AlreadyDecoded, $"exists: {musicIdCandidate}");
                        _logger.Debug("Skip decision: {Path} -> {Reason}", sourcePath, decision.Reason);
                        return decision;
                    }

                    // Online: musicId-only output exists but no fresh marker. Process
                    // exactly once; the next poll's Branch 3b will short-circuit.
                    _logger.Debug(
                        "Skip-edge bootstrap: musicId-only output exists, no fresh marker; processing once");
                }
            }
        }

        // 4. SourceUnchangedSinceFailure — scan .freebird-failed/<stem>.*.txt sidecars.
        var failedDir = Path.Combine(options.OutputDir, FailedDirName);
        if (Directory.Exists(failedDir))
        {
            string[] candidateSidecars;
            try
            {
                candidateSidecars = Directory.GetFiles(failedDir, stem + ".*.txt");
            }
            catch (IOException)
            {
                candidateSidecars = Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                candidateSidecars = Array.Empty<string>();
            }

            var currentMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

            foreach (var sidecarPath in candidateSidecars)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SidecarRecord? record;
                try
                {
                    record = await _sidecarReader.TryReadAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (record is null)
                {
                    continue;
                }
                if (record.SourceSize is null || record.SourceMtime is null)
                {
                    // v1 sidecar — no size/mtime evidence to compare against. Don't skip on this.
                    continue;
                }

                if (record.SourceSize == info.Length && record.SourceMtime == currentMtime)
                {
                    var decision = SkipDecision.Skip(
                        SkipReason.SourceUnchangedSinceFailure,
                        $"matches sidecar: {sidecarPath}");
                    _logger.Debug("Skip decision: {Path} -> {Reason}", sourcePath, decision.Reason);
                    return decision;
                }
            }
        }

        // 5. Default: Process
        _logger.Debug("Skip decision: {Path} -> Process", sourcePath);
        return SkipDecision.Process();
    }
}
