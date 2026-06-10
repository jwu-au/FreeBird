using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Models;
using FreeBird.Core.Naming;
using FreeBird.Core.Processing;
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
    private const int SupportedMarkerSchema = 1;

    private readonly ISidecarReader _sidecarReader;
    private readonly ResolutionMarkerSerializer _markerSerializer;
    private readonly ILogger _logger;

    public FilesystemSkipDecider(
        ISidecarReader sidecarReader,
        ResolutionMarkerSerializer markerSerializer,
        ILogger logger)
    {
        _sidecarReader = sidecarReader ?? throw new ArgumentNullException(nameof(sidecarReader));
        _markerSerializer = markerSerializer ?? throw new ArgumentNullException(nameof(markerSerializer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        var markerDecision = TryShortCircuitOnMarker(sourcePath, stem, info, options);
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

    /// <summary>
    /// Branch 3b implementation (v3.0.1, T05). Returns a non-null <see cref="SkipDecision"/>
    /// only when ALL marker checks pass; returns <c>null</c> to indicate the caller should
    /// fall through to Branch 3c (musicId-only fallback) and the legacy sidecar check.
    ///
    /// Checks performed (in order, short-circuit on any failure):
    ///   1. Marker file exists at &lt;outputDir&gt;/.freebird-resolved/&lt;stem&gt;.json
    ///   2. Marker parses (lenient — TryRead logs WRN on parse failure itself)
    ///   3. Marker schema is present and ≤ <see cref="SupportedMarkerSchema"/>
    ///   4. Freshness: marker.SourceSize == current AND marker.SourceMtime == current
    ///   5. Naming-template match (D3): marker.NamingTemplate == options.NamingTemplate
    ///   6. Retry gate (D4): for non-Resolved status, marker.RetryAfter must NOT have elapsed
    ///   7. Belt-and-suspenders: the referenced output file still exists
    /// </summary>
    private SkipDecision? TryShortCircuitOnMarker(
        string sourcePath,
        string stem,
        FileInfo info,
        WatchOptions options)
    {
        var markerPath = ResolutionMarkerSerializer.MarkerPath(options.OutputDir, stem);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        if (!_markerSerializer.TryRead(markerPath, out var marker) || marker is null)
        {
            // Serializer already logged WRN on parse failure; do NOT log a second WRN.
            return null;
        }

        if (marker.Schema <= 0 || marker.Schema > SupportedMarkerSchema)
        {
            _logger.Warning(
                "Marker at {MarkerPath} has unsupported schema {Schema}; ignoring",
                markerPath, marker.Schema);
            return null;
        }

        // Freshness — source size + mtime.
        var currentMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        if (marker.SourceSize != info.Length || marker.SourceMtime != currentMtime)
        {
            _logger.Information(
                "marker stale (source changed): re-processing {SourcePath}",
                sourcePath);
            return null;
        }

        // Template-change detection (D3).
        if (!string.Equals(marker.NamingTemplate, options.NamingTemplate, StringComparison.Ordinal))
        {
            _logger.Information(
                "marker stale (naming template changed from '{OldTemplate}' to '{NewTemplate}'): re-processing {SourcePath}",
                marker.NamingTemplate, options.NamingTemplate, sourcePath);
            return null;
        }

        // Retry gate (D4) — only applies to non-Resolved statuses with non-null RetryAfter.
        if (marker.Status != MarkerStatus.Resolved
            && marker.RetryAfter is not null
            && DateTimeOffset.Now >= marker.RetryAfter.Value)
        {
            var elapsed = DateTimeOffset.Now - marker.RetryAfter.Value;
            _logger.Information(
                "marker retry-after elapsed ({Status}, retried after {Duration}): re-processing {SourcePath}",
                marker.Status, elapsed, sourcePath);
            return null;
        }

        // Belt-and-suspenders: confirm the output file still exists.
        var outputPath = Path.Combine(options.OutputDir, marker.OutputName);
        if (!File.Exists(outputPath))
        {
            _logger.Information(
                "marker references missing output '{OutputName}': re-processing {SourcePath}",
                marker.OutputName, sourcePath);
            return null;
        }

        // All checks passed — short-circuit.
        _logger.Debug(
            "marker hit: {SourcePath} ({Status}, output={OutputName})",
            sourcePath, marker.Status, marker.OutputName);
        return SkipDecision.Skip(SkipReason.AlreadyDecodedViaMarker, markerPath);
    }
}
