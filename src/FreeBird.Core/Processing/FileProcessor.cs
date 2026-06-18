using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using FreeBird.Core.Naming;
using FreeBird.Core.Watch;
using Serilog;

namespace FreeBird.Core.Processing;

/// <summary>
/// End-to-end pipeline for a single .uc / .uc! file:
/// decrypt -> sniff -> integrity -> atomic write or quarantine.
/// </summary>
public sealed class FileProcessor : IFileProcessor
{
    private const string StagingSubdir = ".freebird-staging";
    private const string FailedSubdir = ".freebird-failed";

    private readonly IXorDecoder _decoder;
    private readonly IFormatSniffer _sniffer;
    private readonly IFileNamer _naming;
    private readonly ICompositeIntegrityChecker _integrity;
    private readonly IAtomicFileWriter _writer;
    private readonly IMetadataResolver _metadata;
    private readonly ITagWriter _tagWriter;
    private readonly ResolutionMarkerSerializer _markerSerializer;
    private readonly IOutputPathMutexPool _outputMutexPool;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public FileProcessor(
        IXorDecoder decoder,
        IFormatSniffer sniffer,
        IFileNamer naming,
        ICompositeIntegrityChecker integrity,
        IAtomicFileWriter writer,
        IMetadataResolver metadata,
        ITagWriter tagWriter,
        ResolutionMarkerSerializer markerSerializer,
        IOutputPathMutexPool outputMutexPool,
        ILogger logger,
        TimeProvider timeProvider)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _sniffer = sniffer ?? throw new ArgumentNullException(nameof(sniffer));
        _naming = naming ?? throw new ArgumentNullException(nameof(naming));
        _integrity = integrity ?? throw new ArgumentNullException(nameof(integrity));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _tagWriter = tagWriter ?? throw new ArgumentNullException(nameof(tagWriter));
        _markerSerializer = markerSerializer ?? throw new ArgumentNullException(nameof(markerSerializer));
        _outputMutexPool = outputMutexPool ?? throw new ArgumentNullException(nameof(outputMutexPool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ScanResult> ProcessAsync(
        string sourcePath,
        ScanOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);

        if (!File.Exists(sourcePath))
        {
            return new ScanResult(sourcePath, ScanOutcome.Error, Reason: $"Source not found: {sourcePath}");
        }

        var stagingDir = Path.Combine(options.OutputDirectory, StagingSubdir);
        var failedDir = Path.Combine(options.OutputDirectory, FailedSubdir);
        Directory.CreateDirectory(stagingDir);

        var stagingPath = Path.Combine(stagingDir, $"{Guid.NewGuid():N}.bin");

        // Step 2: decrypt to staging
        try
        {
            await _writer.WriteAsync(stagingPath, async (output, ct) =>
            {
                await using var input = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    useAsync: true);
                await _decoder.DecodeAsync(input, output, ct).ConfigureAwait(false);
            }, overwrite: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(stagingPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(stagingPath);
            return new ScanResult(sourcePath, ScanOutcome.Error, Reason: $"Decrypt failed: {ex.Message}");
        }

        // Source stem is used by quarantine naming, OK-path naming, and the JSON
        // resolution marker. Compute once via the shared StemBasedFileNamer helper
        // so all three downstream consumers agree on the same stem string.
        var sourceStem = StemBasedFileNamer.GetStem(sourcePath);

        try
        {
            // Step 3: sniff format
            var format = await _sniffer.SniffAsync(stagingPath, cancellationToken).ConfigureAwait(false);

            // Step 4: unknown -> quarantine
            if (format == AudioFormat.Unknown)
            {
                var quarantinedPath = QuarantineFile(
                    stagingPath, failedDir, $"{sourceStem}.bin",
                    sourcePath, AudioFormat.Unknown, levelApplied: null, reason: "Unknown format");
                return new ScanResult(
                    sourcePath,
                    ScanOutcome.UnknownFormat,
                    Format: AudioFormat.Unknown,
                    OutputPath: quarantinedPath,
                    Reason: "Unknown format");
            }

            // Step 4.5: rename staging file to include the correct extension so integrity
            // checks (TagLib#, flac -t) can identify the format from the extension.
            var stagingExt = GetExtensionForFormat(format);
            var stagingPathWithExt = stagingPath + stagingExt;
            File.Move(stagingPath, stagingPathWithExt);
            stagingPath = stagingPathWithExt;

            // Step 5: integrity
            var integrity = await _integrity.CheckAsync(stagingPath, format, options.Integrity, cancellationToken)
                .ConfigureAwait(false);

            // Step 6: integrity failed -> quarantine
            if (!integrity.Ok)
            {
                // v3.2: use sourceStem (not metadata-driven naming) so the quarantine
                // filename matches FilesystemSkipDecider's stem glob, preventing watch
                // mode from re-decoding permanently-failed files on every poll cycle.
                // Also disambiguates multi-bitrate cache files with the same musicId.
                // Mirrors the UnknownFormat sibling path at line 114-115.
                var quarantinedExtension = GetExtensionForFormat(format);
                var outputName = $"{sourceStem}{quarantinedExtension}";
                var quarantinedPath = QuarantineFile(
                    stagingPath, failedDir, outputName,
                    sourcePath, format, integrity.LevelApplied, integrity.Reason ?? "Integrity failed");
                return new ScanResult(
                    sourcePath,
                    ScanOutcome.IntegrityFailed,
                    Format: format,
                    OutputPath: quarantinedPath,
                    Integrity: integrity,
                    Reason: integrity.Reason);
            }

            // Step 6.5 (v3 T14): resolve metadata BEFORE naming. Pass the per-run options so
            // the resolver honors --offline and the per-run --api-timeout. The resolver never
            // throws — errors map to MetadataResolution.Fallback with a sidecar reason.
            var resolution = await _metadata.ResolveAsync(sourcePath, options, cancellationToken).ConfigureAwait(false);
            SongInfo? song = resolution is MetadataResolution.Success s ? s.Song : null;

            // Step 7: compute final path.
            // v3 T19a: per-run NamingTemplate is now threaded into the namer via the method param.
            var finalName = _naming.GetTargetName(sourcePath, format, song, options.NamingTemplate);
            var finalPath = Path.Combine(options.OutputDirectory, finalName);

            // v3.4 T12: serialise the skip-check + atomic move + tag-write + marker-write
            // critical section by finalPath. Two concurrent watches against different input
            // dirs that produce the SAME output filename (e.g. same musicId in two cache dirs)
            // would otherwise race here:
            //   - both see File.Exists==false in Step 8,
            //   - both File.Move to finalPath in Step 9 (clobber on Overwrite, throw on Skip),
            //   - both metaflac on finalPath in Step 9.5 (tag corruption).
            // Acquiring the per-path mutex BEFORE Step 8 makes the exists-check TOCTOU-safe
            // and forces the second caller to serialise behind the first. The `using`
            // declaration scopes the lock to the rest of the try-block — released on every
            // exit (return, throw, fall-through).
            using var outputMutexToken = await _outputMutexPool
                .AcquireAsync(finalPath, cancellationToken)
                .ConfigureAwait(false);

            // Step 8: collision check
            if (File.Exists(finalPath) && options.OnCollision == CollisionPolicy.Skip)
            {
                TryDelete(stagingPath);

                // v3.0.1 T04 / D7(a): on collision-skip we still write a marker so
                // the watch skip decider recognizes the existing file as resolved
                // and stops infinite re-processing. OutputName is the existing
                // colliding file's basename (which is identical to finalPath here).
                if (!options.Offline)
                {
                    var skipMarker = BuildMarker(
                        sourcePath, sourceStem, finalPath, format,
                        integrity.LevelApplied,
                        MapToMarkerStatus(resolution),
                        options.NamingTemplate,
                        tagWriteStatus: null,
                        tagWriteReason: null);
                    _markerSerializer.WriteAtomic(options.OutputDirectory, skipMarker);
                }

                return new ScanResult(
                    sourcePath,
                    ScanOutcome.Skipped,
                    Format: format,
                    OutputPath: finalPath,
                    Integrity: integrity,
                    Reason: "Output already exists (skip policy)");
            }

            // Step 9: move staging -> final (atomic on same filesystem)
            Directory.CreateDirectory(options.OutputDirectory);
            File.Move(stagingPath, finalPath, overwrite: options.OnCollision == CollisionPolicy.Overwrite);

            // Step 9.5 (v3 T18): optional post-rename tag-write.
            //   * Only fires when WriteTags=true AND metadata resolved successfully.
            //   * Failure is captured in the JSON marker but NEVER deletes/rolls-back
            //     the decoded audio file — the user's primary artifact is preserved.
            //   * Catch-all Exception is intentional: ITagWriter is a long-tail of
            //     possible failure modes (missing tool, corrupt file, locking, etc.)
            //     and the contract is "never lose decoded audio".
            //
            // v3.0.1 T04: the legacy `<output>.flac.txt` sidecar is gone. Tag-write
            // outcomes now live in the JSON marker via the TagWriteStatus / TagWriteReason
            // fields (Option A additive schema).
            string? tagWriteStatus = null;
            string? tagWriteReason = null;
            if (options.WriteTags)
            {
                if (song is null)
                {
                    // Metadata fell back — there is nothing to tag. Skipped, not failed.
                    tagWriteStatus = "skipped";
                }
                else
                {
                    try
                    {
                        var tagResult = await _tagWriter
                            .WriteAsync(finalPath, format, song, cancellationToken)
                            .ConfigureAwait(false);
                        if (tagResult is TagWriteResult.Failed failed)
                        {
                            tagWriteStatus = "failed";
                            tagWriteReason = failed.SidecarReason;
                            _logger.Warning(
                                "Tag write failed for {Path}: {Reason}",
                                finalPath, failed.SidecarReason);
                        }
                        else
                        {
                            tagWriteStatus = "ok";
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (FreeBird.Core.Provisioning.FlacNotAvailableException ex)
                    {
                        // T13: metaflac unavailable (race with startup probe, or probe was
                        // skipped). Tag write is decorative — never kill the file. Record
                        // the well-known sidecar reason so downstream tools / users see
                        // the standard "tag-tool-missing" string.
                        tagWriteStatus = "failed";
                        tagWriteReason = "tag-tool-missing";
                        _logger.Warning(
                            "Tag write skipped for {Path}: metaflac unavailable ({Message})",
                            finalPath, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        tagWriteStatus = "failed";
                        tagWriteReason = "tag-write-failed";
                        _logger.Warning(ex, "Unexpected tag-write error for {Path}", finalPath);
                    }
                }
            }

            // Step 9.6 (v3.0.1 T04): write the JSON resolution marker.
            //   * Suppressed entirely when --offline (the user has opted out of
            //     metadata resolution; markers would be misleading and may shadow
            //     a real marker from a non-offline run — see D5).
            //   * Otherwise emitted for EVERY outcome (success, metadata-empty,
            //     metadata-fetch-failed, metadata-deserialize-failed) so the watch
            //     skip decider can suppress infinite re-processing.
            //   * The marker is the LAST step. If audio File.Move above threw,
            //     this code is unreachable and no marker is written — preserving
            //     the invariant that markers only reference real on-disk audio.
            if (!options.Offline)
            {
                var marker = BuildMarker(
                    sourcePath, sourceStem, finalPath, format,
                    integrity.LevelApplied,
                    MapToMarkerStatus(resolution),
                    options.NamingTemplate,
                    tagWriteStatus,
                    tagWriteReason);
                _markerSerializer.WriteAtomic(options.OutputDirectory, marker);
            }

            return new ScanResult(
                sourcePath,
                ScanOutcome.Ok,
                Format: format,
                OutputPath: finalPath,
                Integrity: integrity);
        }
        catch (OperationCanceledException)
        {
            TryDelete(stagingPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(stagingPath);
            return new ScanResult(sourcePath, ScanOutcome.Error, Reason: $"Processing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Move the staging file into the failed/quarantine directory and write the spec'd 7-field
    /// sidecar metadata next to it. The sidecar is written first to a `.tmp` file so that if any
    /// step fails the staging file is not stranded with no record (C1 ship-blocker).
    /// </summary>
    private string QuarantineFile(
        string stagingPath,
        string failedDir,
        string fileName,
        string sourcePath,
        AudioFormat format,
        IntegrityLevel? levelApplied,
        string reason)
    {
        Directory.CreateDirectory(failedDir);
        var dest = Path.Combine(failedDir, fileName);
        var sidecarPath = dest + ".txt";
        var sidecarTmp = sidecarPath + ".tmp";
        var sidecarContent = BuildSidecarContent(sourcePath, format, levelApplied, reason, _logger);

        // 1) Write sidecar to a .tmp file first — if this fails, the staging file remains in place.
        File.WriteAllText(sidecarTmp, sidecarContent);

        try
        {
            // 2) Move staging file to its final quarantine destination (overwrite if needed).
            if (File.Exists(dest)) { File.Delete(dest); }
            File.Move(stagingPath, dest);

            // 3) Atomically rename sidecar.tmp -> sidecar to commit the metadata record.
            if (File.Exists(sidecarPath)) { File.Delete(sidecarPath); }
            File.Move(sidecarTmp, sidecarPath);
        }
        catch
        {
            // Clean up half-written sidecar tmp on any failure to keep failed/ tidy.
            try { if (File.Exists(sidecarTmp)) { File.Delete(sidecarTmp); } } catch { /* best-effort */ }
            throw;
        }

        return dest;
    }

    /// <summary>
    /// Build the 7-field v2 sidecar content. Fields 1–5 mirror v1; <c>source_size</c> and
    /// <c>source_mtime</c> are added for v2 watch-mode skip decisions (T07).
    ///
    /// Race-handling (per brainstorm Q-Amb-3): if the source file has vanished between decrypt
    /// and sidecar-write, we still emit a sidecar but with size=0 and mtime=epoch. This guarantees
    /// the user has a record of the failure even if the source is gone; the unusual values let the
    /// future <c>FilesystemSkipDecider</c> treat the sidecar as "never matches a real file".
    /// </summary>
    internal static string BuildSidecarContent(
        string sourcePath,
        AudioFormat format,
        IntegrityLevel? levelApplied,
        string reason,
        ILogger logger)
    {
        long sourceSize;
        DateTimeOffset sourceMtime;
        try
        {
            var info = new FileInfo(sourcePath);
            if (info.Exists)
            {
                sourceSize = info.Length;
                sourceMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            }
            else
            {
                logger.Warning("Source file vanished before sidecar write: {SourcePath}", sourcePath);
                sourceSize = 0;
                sourceMtime = DateTimeOffset.UnixEpoch;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Failed to stat source file before sidecar write: {SourcePath}", sourcePath);
            sourceSize = 0;
            sourceMtime = DateTimeOffset.UnixEpoch;
        }

        var sb = new StringBuilder();
        sb.Append("timestamp: ").AppendLine(DateTime.UtcNow.ToString("O"));
        sb.AppendLine("version: 3");
        sb.Append("source:    ").AppendLine(sourcePath);
        sb.Append("format:    ").AppendLine(format.ToString());
        sb.Append("integrity: ").AppendLine(levelApplied?.ToString() ?? "-");
        sb.Append("reason:    ").AppendLine(reason);
        sb.Append("source_size: ").AppendLine(sourceSize.ToString(CultureInfo.InvariantCulture));
        sb.Append("source_mtime: ").AppendLine(sourceMtime.ToString("O", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Test-only forwarder. Internal access is granted via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal static string BuildSidecarContentForTesting(
        string sourcePath,
        AudioFormat format,
        IntegrityLevel? levelApplied,
        string reason,
        ILogger logger)
        => BuildSidecarContent(sourcePath, format, levelApplied, reason, logger);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } }
        catch { /* best-effort */ }
    }

    private static string GetExtensionForFormat(AudioFormat format) => format switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Flac => ".flac",
        AudioFormat.M4a => ".m4a",
        _ => ".bin",
    };

    /// <summary>
    /// Map a <see cref="MetadataResolution"/> outcome into a <see cref="MarkerStatus"/>
    /// for the JSON resolution marker. Caller MUST suppress the marker for
    /// <c>options.Offline == true</c>; this method treats the offline reason as a
    /// programmer error because the offline branch should never reach a marker write.
    /// Unknown fallback reasons are conservatively mapped to <see cref="MarkerStatus.MetadataFetchFailed"/>
    /// (the most common transient failure class) with a DBG log.
    /// </summary>
    private MarkerStatus MapToMarkerStatus(MetadataResolution resolution)
    {
        switch (resolution)
        {
            case MetadataResolution.Success:
                return MarkerStatus.Resolved;
            case MetadataResolution.Fallback fb:
                return fb.SidecarReason switch
                {
                    "metadata-empty" => MarkerStatus.MetadataEmpty,
                    "metadata-fetch-failed" => MarkerStatus.MetadataFetchFailed,
                    "metadata-rate-limited" => MarkerStatus.MetadataRateLimited,
                    "metadata-deserialize-failed" => MarkerStatus.MetadataDeserializeFailed,
                    "offline-mode" => throw new InvalidOperationException(
                        "MapToMarkerStatus must not be called for offline-mode fallback; the caller is required to skip marker writes when Offline=true."),
                    _ => MapUnknownFallback(fb.SidecarReason),
                };
            default:
                throw new InvalidOperationException($"Unknown MetadataResolution variant: {resolution.GetType().Name}");
        }
    }

    private MarkerStatus MapUnknownFallback(string reason)
    {
        _logger.Debug(
            "Unrecognized MetadataResolution.Fallback reason '{Reason}'; mapping to MetadataFetchFailed for marker",
            reason);
        return MarkerStatus.MetadataFetchFailed;
    }

    /// <summary>
    /// Build a <see cref="ResolutionMarker"/> from the per-attempt context. Centralizes
    /// the field mapping so the OK path, the collision-skip path, and (in future) the
    /// L1-fail path all serialize identical schemas.
    ///
    /// Source size/mtime are stat'd here, mirroring the race-handling rule used by
    /// <see cref="BuildSidecarContent"/>: if the source has vanished, we fall back to
    /// size=0 + mtime=epoch and log a WARN. The unusual values make the marker
    /// recognizable as "source disappeared mid-flight" rather than corrupting the
    /// freshness key.
    /// </summary>
    private ResolutionMarker BuildMarker(
        string sourcePath,
        string sourceStem,
        string finalPath,
        AudioFormat format,
        IntegrityLevel levelApplied,
        MarkerStatus status,
        string namingTemplate,
        string? tagWriteStatus,
        string? tagWriteReason)
    {
        long sourceSize;
        DateTimeOffset sourceMtime;
        try
        {
            var info = new FileInfo(sourcePath);
            if (info.Exists)
            {
                sourceSize = info.Length;
                sourceMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            }
            else
            {
                _logger.Warning("Source file vanished before marker write: {SourcePath}", sourcePath);
                sourceSize = 0;
                sourceMtime = DateTimeOffset.UnixEpoch;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Failed to stat source file before marker write: {SourcePath}", sourcePath);
            sourceSize = 0;
            sourceMtime = DateTimeOffset.UnixEpoch;
        }

        var resolvedAt = _timeProvider.GetUtcNow();
        // T3: attempt-aware ladder. FileProcessor does not yet read prior-attempt
        // count (T5) or a server Retry-After (T4); a first-failure marker uses the
        // first rung (attemptCount: 1, serverRetryAfter: null) as the placeholder.
        var retry = ResolutionMarkerRetry.For(status, attemptCount: 1, serverRetryAfter: null);
        DateTimeOffset? retryAfter = retry.HasValue ? resolvedAt + retry.Value : null;

        string? reason = status switch
        {
            MarkerStatus.Resolved => null,
            MarkerStatus.MetadataEmpty => "metadata-empty",
            MarkerStatus.MetadataFetchFailed => "metadata-fetch-failed",
            MarkerStatus.MetadataRateLimited => "metadata-rate-limited",
            MarkerStatus.MetadataDeserializeFailed => "metadata-deserialize-failed",
            _ => throw new InvalidOperationException($"Unknown MarkerStatus: {status}"),
        };

        return new ResolutionMarker
        {
            Schema = 2,
            SourceStem = sourceStem,
            MusicId = MusicIdExtractor.TryExtractAsString(sourcePath) ?? "",
            SourcePath = sourcePath,
            SourceSize = sourceSize,
            SourceMtime = sourceMtime,
            ResolvedAt = resolvedAt,
            Status = status,
            OutputName = Path.GetFileName(finalPath),
            Format = format.ToString(),
            Integrity = levelApplied.ToString(),
            NamingTemplate = namingTemplate,
            Reason = reason,
            RetryAfter = retryAfter,
            TagWriteStatus = tagWriteStatus,
            TagWriteReason = tagWriteReason,
        };
    }
}
