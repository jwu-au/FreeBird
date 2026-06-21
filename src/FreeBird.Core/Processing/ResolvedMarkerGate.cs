using System;
using System.IO;
using System.Threading;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Models;
using Serilog;

namespace FreeBird.Core.Processing;

/// <summary>
/// Single source of truth for the marker short-circuit (formerly
/// <c>FilesystemSkipDecider.TryShortCircuitOnMarker</c>, Branch 3b). The logic is
/// reproduced verbatim, parameterized by <c>(outputDir, namingTemplate)</c> rather
/// than <c>WatchOptions</c> so it can be shared by scan and watch.
///
/// Stateless (only logger/serializer/timeProvider fields) → registered via the
/// <see cref="IDependency"/> auto-scan as <c>InstancePerLifetimeScope</c>; no
/// <c>SingleInstance</c> carve-out needed.
/// </summary>
public sealed class ResolvedMarkerGate : IResolvedMarkerGate
{
    private const int SupportedMarkerSchema = 2;

    private readonly ResolutionMarkerSerializer _markerSerializer;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public ResolvedMarkerGate(
        ResolutionMarkerSerializer markerSerializer,
        ILogger logger,
        TimeProvider timeProvider)
    {
        _markerSerializer = markerSerializer ?? throw new ArgumentNullException(nameof(markerSerializer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public SkipDecision? TryShortCircuit(
        string sourcePath,
        string outputDir,
        string namingTemplate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var stem = StemBasedFileNamer.GetStem(sourcePath);

        // ponytail: the gate re-stats the source (the watch decider previously
        // passed a pre-stat'd FileInfo in). The extra stat on the watch path is
        // negligible; upgrade path = thread a FileInfo parameter through if it
        // ever shows up on a profile.
        FileInfo info;
        try
        {
            info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                return null;
            }
            // Touch Length/LastWriteTimeUtc now so any IO error surfaces here.
            _ = info.Length;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Defensive: cannot stat the source → fall through (process).
            return null;
        }

        var markerPath = ResolutionMarkerSerializer.MarkerPath(outputDir, stem);
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
        if (!string.Equals(marker.NamingTemplate, namingTemplate, StringComparison.Ordinal))
        {
            _logger.Information(
                "marker stale (naming template changed from '{OldTemplate}' to '{NewTemplate}'): re-processing {SourcePath}",
                marker.NamingTemplate, namingTemplate, sourcePath);
            return null;
        }

        // Retry gate (D4) — only applies to non-Resolved statuses with non-null RetryAfter.
        if (marker.Status != MarkerStatus.Resolved
            && marker.RetryAfter is not null
            && _timeProvider.GetUtcNow() >= marker.RetryAfter.Value)
        {
            var elapsed = _timeProvider.GetUtcNow() - marker.RetryAfter.Value;
            _logger.Information(
                "marker retry-after elapsed ({Status}, retried after {Duration}): re-processing {SourcePath}",
                marker.Status, elapsed, sourcePath);
            return null;
        }

        // Belt-and-suspenders: confirm the output file still exists.
        var outputPath = Path.Combine(outputDir, marker.OutputName);
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
