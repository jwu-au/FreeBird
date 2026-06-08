using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Models;

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
    private readonly INamingStrategy _naming;
    private readonly ICompositeIntegrityChecker _integrity;
    private readonly IAtomicFileWriter _writer;

    public FileProcessor(
        IXorDecoder decoder,
        IFormatSniffer sniffer,
        INamingStrategy naming,
        ICompositeIntegrityChecker integrity,
        IAtomicFileWriter writer)
    {
        _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        _sniffer = sniffer ?? throw new ArgumentNullException(nameof(sniffer));
        _naming = naming ?? throw new ArgumentNullException(nameof(naming));
        _integrity = integrity ?? throw new ArgumentNullException(nameof(integrity));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
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
                var sourceStem = StemPlusExtensionNamingStrategy.GetStem(sourcePath);
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
                var outputName = _naming.GetOutputFileName(sourcePath, format);
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

            // Step 7: compute final path
            var finalName = _naming.GetOutputFileName(sourcePath, format);
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
    /// Move the staging file into the failed/quarantine directory and write the spec'd 5-field
    /// sidecar metadata next to it. The sidecar is written first to a `.tmp` file so that if any
    /// step fails the staging file is not stranded with no record (C1 ship-blocker).
    /// </summary>
    private static string QuarantineFile(
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
        var sidecarContent = BuildSidecarContent(sourcePath, format, levelApplied, reason);

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

    private static string BuildSidecarContent(
        string sourcePath,
        AudioFormat format,
        IntegrityLevel? levelApplied,
        string reason)
    {
        var sb = new StringBuilder();
        sb.Append("timestamp: ").AppendLine(DateTime.UtcNow.ToString("O"));
        sb.Append("source:    ").AppendLine(sourcePath);
        sb.Append("format:    ").AppendLine(format.ToString());
        sb.Append("integrity: ").AppendLine(levelApplied?.ToString() ?? "-");
        sb.Append("reason:    ").AppendLine(reason);
        return sb.ToString();
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
