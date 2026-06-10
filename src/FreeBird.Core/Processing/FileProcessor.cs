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
    private readonly ILogger _logger;

    public FileProcessor(
        IXorDecoder decoder,
        IFormatSniffer sniffer,
        IFileNamer naming,
        ICompositeIntegrityChecker integrity,
        IAtomicFileWriter writer,
        IMetadataResolver metadata,
        ILogger logger)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _sniffer = sniffer ?? throw new ArgumentNullException(nameof(sniffer));
        _naming = naming ?? throw new ArgumentNullException(nameof(naming));
        _integrity = integrity ?? throw new ArgumentNullException(nameof(integrity));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        try
        {
            // Step 3: sniff format
            var format = await _sniffer.SniffAsync(stagingPath, cancellationToken).ConfigureAwait(false);

            // Step 4: unknown -> quarantine
            if (format == AudioFormat.Unknown)
            {
                // Use the naming strategy's stem extractor for consistency with the OK-path naming
                // (cross-runtime safe; .NET 10 + Mono variants handle .uc! differently in Path.GetFileNameWithoutExtension).
                var sourceStem = StemBasedFileNamer.GetStem(sourcePath);
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
                var outputName = _naming.GetTargetName(sourcePath, format, metadata: null);
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
            // TODO(v3 T18/T19): per-run NamingTemplate from options is currently overridden by
            // DefaultMetadataOptions; CLI flag plumbing tasks will bridge per-run options into
            // MetadataAwareFileNamer (via child lifetime scope, method param, or per-call namer).
            var finalName = _naming.GetTargetName(sourcePath, format, song);
            var finalPath = Path.Combine(options.OutputDirectory, finalName);

            // Step 8: collision check
            if (File.Exists(finalPath) && options.OnCollision == CollisionPolicy.Skip)
            {
                TryDelete(stagingPath);
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

            // Step 9.5 (v3 T14): emit the v3 OK-path sidecar for non-offline metadata fallback.
            // Spec §10 special case: "offline-mode" never gets a sidecar — the user opted out
            // of metadata explicitly, so the fallback naming is by design, not a failure.
            // Sidecar content uses the same 7-field format as the v2 quarantine sidecar for
            // consistency. The OK-path sidecar lives next to the SUCCESSFULLY decoded output
            // (in OutputDirectory), not in the .freebird-failed quarantine directory.
            if (resolution is MetadataResolution.Fallback fb && fb.SidecarReason != "offline-mode")
            {
                var sidecarPath = finalPath + ".txt";
                var sidecarContent = BuildSidecarContent(
                    sourcePath, format, integrity.LevelApplied, fb.SidecarReason, _logger);
                File.WriteAllText(sidecarPath, sidecarContent);
                _logger.Debug(
                    "Wrote v3 metadata-fallback sidecar: {Path} (reason={Reason})",
                    sidecarPath, fb.SidecarReason);
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
}
