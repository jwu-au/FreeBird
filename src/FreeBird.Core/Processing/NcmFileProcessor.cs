using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using FreeBird.Core.Naming;
using FreeBird.Core.Provisioning;
using FreeBird.Core.Watch;
using Serilog;

namespace FreeBird.Core.Processing;

/// <summary>
/// End-to-end pipeline for a single NetEase <c>.ncm</c> file:
/// decode (AES/RC4 + embedded metadata + cover) -> sniff -> integrity -> atomic
/// write or quarantine -> embed tags/cover -> resolution marker.
///
/// Mirrors the structure of the <c>.uc</c> <see cref="FileProcessor"/> but carries
/// NO NetEase API / rate-limiter / retry machinery: NCM metadata and cover art are
/// embedded in the container, so there is no network step. The <c>.uc</c>
/// <see cref="FileProcessor"/> is intentionally left byte-for-byte unchanged.
/// </summary>
public sealed class NcmFileProcessor : IFileProcessor
{
    // Reuse the SAME subdir names as FileProcessor so watch-mode skip logic and the
    // user-visible layout are identical across .uc and .ncm.
    private const string StagingSubdir = ".freebird-staging";
    private const string FailedSubdir = ".freebird-failed";

    private readonly INcmDecoder _decoder;
    private readonly IFormatSniffer _sniffer;
    private readonly ICompositeIntegrityChecker _integrity;
    private readonly IAtomicFileWriter _writer;
    private readonly ITagWriter _tagWriter;
    private readonly ICoverWriter _coverWriter;
    private readonly ResolutionMarkerSerializer _markerSerializer;
    private readonly IOutputPathMutexPool _outputMutexPool;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    public NcmFileProcessor(
        INcmDecoder decoder,
        IFormatSniffer sniffer,
        ICompositeIntegrityChecker integrity,
        IAtomicFileWriter writer,
        ITagWriter tagWriter,
        ICoverWriter coverWriter,
        ResolutionMarkerSerializer markerSerializer,
        IOutputPathMutexPool outputMutexPool,
        ILogger logger,
        TimeProvider timeProvider)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _sniffer = sniffer ?? throw new ArgumentNullException(nameof(sniffer));
        _integrity = integrity ?? throw new ArgumentNullException(nameof(integrity));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _tagWriter = tagWriter ?? throw new ArgumentNullException(nameof(tagWriter));
        _coverWriter = coverWriter ?? throw new ArgumentNullException(nameof(coverWriter));
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

        // Source stem drives quarantine naming, the fallback output name, and the
        // resolution marker key — computed once so all consumers agree.
        var sourceStem = StemBasedFileNamer.GetStem(sourcePath);

        // Step 2: decode to staging. The decoder writes the decrypted audio BODY to the
        // output stream and returns metadata + cover via the captured local.
        NcmDecodeResult? decodeResult = null;
        try
        {
            await _writer.WriteAsync(stagingPath, async (output, ct) =>
            {
                decodeResult = await _decoder.DecodeAsync(sourcePath, output, ct).ConfigureAwait(false);
            }, overwrite: false, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryDelete(stagingPath);
            throw;
        }
        catch (NcmDecodeException ex)
        {
            // A bad-magic / corrupt / AES / truncated .ncm is a PERMANENT failure: the
            // body in staging (if any) is unusable. Unlike the .uc decrypt-failure path
            // (which returns a transient-looking Error and never quarantines), watch must
            // SKIP a permanently-bad .ncm on subsequent polls. FilesystemSkipDecider's
            // failed-file branch globs ".freebird-failed/<stem>.*.txt" and matches on
            // source_size + source_mtime — it never reads a payload file. So a
            // sidecar-only quarantine record is sufficient and correct here.
            TryDelete(stagingPath);
            WriteFailureSidecar(failedDir, $"{sourceStem}.bin", sourcePath, AudioFormat.Unknown, levelApplied: null, reason: ex.Reason);
            return new ScanResult(sourcePath, ScanOutcome.Error, Reason: ex.Reason);
        }
        catch (Exception ex)
        {
            TryDelete(stagingPath);
            return new ScanResult(sourcePath, ScanOutcome.Error, Reason: $"NCM decode failed: {ex.Message}");
        }

        // decodeResult is guaranteed non-null once WriteAsync completes without throwing
        // (the lambda always assigns it before returning). Defensive guard for clarity.
        var result = decodeResult ?? new NcmDecodeResult(null, null, AudioFormat.Unknown);

        try
        {
            // Step 3: sniff format on the staging file (AUTHORITATIVE; the decoder's
            // Format is only a hint, mirroring FileProcessor).
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

            // Step 4.5: rename staging to include the correct extension so integrity
            // tools (TagLib#, flac -t) can identify the format from the extension.
            var stagingExt = GetExtensionForFormat(format);
            var stagingPathWithExt = stagingPath + stagingExt;
            File.Move(stagingPath, stagingPathWithExt);
            stagingPath = stagingPathWithExt;

            // Step 5: integrity
            var integrity = await _integrity.CheckAsync(stagingPath, format, options.Integrity, cancellationToken)
                .ConfigureAwait(false);

            // Step 6: integrity failed -> quarantine (stem-named, matching the skip glob).
            if (!integrity.Ok)
            {
                var outputName = $"{sourceStem}{GetExtensionForFormat(format)}";
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

            // Step 7: naming from EMBEDDED metadata (no IFileNamer/musicId path).
            var song = BuildSongInfo(result.Metadata);
            var finalName = BuildFinalName(result.Metadata, sourcePath, options.NamingTemplate, format);
            var finalPath = Path.Combine(options.OutputDirectory, finalName);

            // v3.4 T12 parity: serialise collision-check + move + tag/cover + marker by finalPath.
            using var outputMutexToken = await _outputMutexPool
                .AcquireAsync(finalPath, cancellationToken)
                .ConfigureAwait(false);

            // Step 8: collision check. NCM is always "Resolved" (no offline guard, no
            // metadata-resolution status mapping).
            if (File.Exists(finalPath) && options.OnCollision == CollisionPolicy.Skip)
            {
                TryDelete(stagingPath);
                WriteMarker(options, sourcePath, sourceStem, finalPath, format, integrity, tagWriteStatus: null, tagWriteReason: null);
                return new ScanResult(
                    sourcePath,
                    ScanOutcome.Skipped,
                    Format: format,
                    OutputPath: finalPath,
                    Integrity: integrity,
                    Reason: "Output already exists (skip policy)");
            }

            // Step 9: move staging -> final (atomic on same filesystem).
            Directory.CreateDirectory(options.OutputDirectory);
            File.Move(stagingPath, finalPath, overwrite: options.OnCollision == CollisionPolicy.Overwrite);

            // Step 9.5: optional tag + cover embed. --no-write-tags suppresses BOTH
            // (cover art is a tag). A tag OR cover failure NEVER deletes the audio.
            string? tagWriteStatus = null;
            string? tagWriteReason = null;
            if (options.WriteTags)
            {
                (tagWriteStatus, tagWriteReason) = await WriteTagsAndCoverAsync(
                    finalPath, format, song, result.Cover, cancellationToken).ConfigureAwait(false);
            }

            // Step 9.6: resolution marker. Always written on success for .ncm (no
            // offline guard). ponytail: NCM does NOT do the .uc prior-marker stale
            // fallback cleanup — names are content-derived and stable, so there is no
            // differently-named orphan to clean. Upgrade path: add cleanup only if a
            // future naming change can produce two names for the same source.
            WriteMarker(options, sourcePath, sourceStem, finalPath, format, integrity, tagWriteStatus, tagWriteReason);

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
    /// Build a <see cref="SongInfo"/> from the embedded NCM metadata. MusicId is not
    /// meaningful for the .ncm path (no NetEase lookup) so it is left 0.
    /// </summary>
    private static SongInfo BuildSongInfo(NcmMetadata? metadata)
    {
        var title = metadata?.MusicName ?? "";
        IReadOnlyList<string> artists = metadata?.Artists ?? Array.Empty<string>();
        return new SongInfo(0, title, artists, metadata?.Album);
    }

    /// <summary>
    /// Compute the final output filename from embedded metadata. When metadata is
    /// present, render the naming template (multi-artist joined with " &amp; ") and
    /// sanitize. When metadata is null/empty, fall back to the source basename minus
    /// the .ncm extension. Always appends the sniffed-format extension.
    /// </summary>
    private static string BuildFinalName(
        NcmMetadata? metadata,
        string sourcePath,
        string namingTemplate,
        AudioFormat format)
    {
        var ext = GetExtensionForFormat(format);

        var title = metadata?.MusicName;
        var artists = metadata?.Artists ?? Array.Empty<string>();
        var hasUsableTitle = !string.IsNullOrWhiteSpace(title);
        var hasUsableArtist = artists.Any(a => !string.IsNullOrWhiteSpace(a));

        if (!hasUsableTitle && !hasUsableArtist)
        {
            // Fallback: source basename with the .ncm suffix stripped (case-insensitive).
            return FilenameSanitizer.Sanitize(StripNcmExtension(sourcePath)) + ext;
        }

        var song = new SongInfo(0, title ?? "", artists, metadata?.Album);
        var rendered = new NamingTemplateRenderer().Render(namingTemplate, song, musicId: 0);
        return FilenameSanitizer.Sanitize(rendered) + ext;
    }

    private static string StripNcmExtension(string sourcePath)
    {
        var filename = Path.GetFileName(sourcePath);
        if (filename.EndsWith(".ncm", StringComparison.OrdinalIgnoreCase))
        {
            return filename[..^4];
        }
        return filename;
    }

    /// <summary>
    /// Embed text tags then cover. Returns the marker status/reason for the text-tag
    /// write. A cover failure is logged + recorded but never propagated as a status
    /// that could mislead — and never deletes the audio.
    /// </summary>
    private async Task<(string? status, string? reason)> WriteTagsAndCoverAsync(
        string finalPath,
        AudioFormat format,
        SongInfo song,
        byte[]? cover,
        CancellationToken cancellationToken)
    {
        string? status = null;
        string? reason = null;

        // (a) text tags
        if (string.IsNullOrWhiteSpace(song.Title) && song.Artists.Count == 0)
        {
            status = "skipped";
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
                    status = "failed";
                    reason = failed.SidecarReason;
                    _logger.Warning("Tag write failed for {Path}: {Reason}", finalPath, failed.SidecarReason);
                }
                else
                {
                    status = "ok";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FlacNotAvailableException ex)
            {
                status = "failed";
                reason = "tag-tool-missing";
                _logger.Warning("Tag write skipped for {Path}: metaflac unavailable ({Message})", finalPath, ex.Message);
            }
            catch (Exception ex)
            {
                status = "failed";
                reason = "tag-write-failed";
                _logger.Warning(ex, "Unexpected tag-write error for {Path}", finalPath);
            }
        }

        // (b) cover — same exception discipline. A cover failure must NEVER delete the audio.
        if (cover is { Length: > 0 })
        {
            try
            {
                var coverResult = await _coverWriter
                    .WriteCoverAsync(finalPath, format, cover, cancellationToken)
                    .ConfigureAwait(false);
                if (coverResult is TagWriteResult.Failed failed)
                {
                    _logger.Warning("Cover write failed for {Path}: {Reason}", finalPath, failed.SidecarReason);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FlacNotAvailableException ex)
            {
                _logger.Warning("Cover write skipped for {Path}: metaflac unavailable ({Message})", finalPath, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Unexpected cover-write error for {Path}", finalPath);
            }
        }

        return (status, reason);
    }

    /// <summary>
    /// Build and write the resolution marker. NCM is always <see cref="MarkerStatus.Resolved"/>,
    /// so all of the .uc retry/attempt-ladder machinery is omitted.
    /// </summary>
    private void WriteMarker(
        ScanOptions options,
        string sourcePath,
        string sourceStem,
        string finalPath,
        AudioFormat format,
        IntegrityResult integrity,
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

        long? outputSize = null;
        DateTimeOffset? outputMtime = null;
        try
        {
            var outInfo = new FileInfo(finalPath);
            if (outInfo.Exists)
            {
                outputSize = outInfo.Length;
                outputMtime = new DateTimeOffset(outInfo.LastWriteTimeUtc, TimeSpan.Zero);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(ex, "Failed to stat output file before marker write: {FinalPath}", finalPath);
        }

        var marker = new ResolutionMarker
        {
            Schema = 2,
            SourceStem = sourceStem,
            // MusicId is a required (non-null) string the skip gate never reads for .ncm.
            MusicId = "",
            SourcePath = sourcePath,
            SourceSize = sourceSize,
            SourceMtime = sourceMtime,
            ResolvedAt = _timeProvider.GetUtcNow(),
            Status = MarkerStatus.Resolved,
            OutputName = Path.GetFileName(finalPath),
            Format = format.ToString(),
            Integrity = integrity.LevelApplied.ToString(),
            NamingTemplate = options.NamingTemplate,
            Reason = null,
            RetryAfter = null,
            AttemptCount = null,
            TagWriteStatus = tagWriteStatus,
            TagWriteReason = tagWriteReason,
            OutputSize = outputSize,
            OutputMtime = outputMtime,
        };
        _markerSerializer.WriteAtomic(options.OutputDirectory, marker);
    }

    /// <summary>
    /// Write JUST the failure sidecar (no payload) into the failed dir. Used on the
    /// permanent NcmDecodeException path so watch's FilesystemSkipDecider (which globs
    /// "&lt;stem&gt;.*.txt" and matches source_size + source_mtime) skips the file on
    /// later polls. Uses <see cref="FileProcessor.BuildSidecarContent"/> directly so
    /// the sidecar fields are byte-identical to the .uc path (no error_class).
    /// </summary>
    private void WriteFailureSidecar(
        string failedDir,
        string fileName,
        string sourcePath,
        AudioFormat format,
        IntegrityLevel? levelApplied,
        string reason)
    {
        try
        {
            Directory.CreateDirectory(failedDir);
            var sidecarPath = Path.Combine(failedDir, fileName) + ".txt";
            var content = FileProcessor.BuildSidecarContent(sourcePath, format, levelApplied, reason, _logger);
            File.WriteAllText(sidecarPath, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a failed sidecar write just means watch re-tries the bad
            // file next poll. Never let it mask the decode failure.
            _logger.Warning(ex, "Failed to write decode-failure sidecar for {SourcePath}", sourcePath);
        }
    }

    // ---- Helpers mirrored from FileProcessor's private members ----------
    // ponytail: these are intentional copies of FileProcessor's private helpers so
    // the .uc class stays byte-for-byte untouched. Upgrade path: extract a shared
    // ProcessingPrimitives helper if a third processor appears.

    /// <summary>
    /// Move the staging file into the quarantine dir and write the spec'd 7-field
    /// sidecar next to it (sidecar written to .tmp first so the staging file is never
    /// stranded without a record).
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
        var sidecarContent = FileProcessor.BuildSidecarContent(sourcePath, format, levelApplied, reason, _logger);

        File.WriteAllText(sidecarTmp, sidecarContent);

        try
        {
            if (File.Exists(dest)) { File.Delete(dest); }
            File.Move(stagingPath, dest);

            if (File.Exists(sidecarPath)) { File.Delete(sidecarPath); }
            File.Move(sidecarTmp, sidecarPath);
        }
        catch
        {
            try { if (File.Exists(sidecarTmp)) { File.Delete(sidecarTmp); } } catch { /* best-effort */ }
            throw;
        }

        return dest;
    }

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
}
