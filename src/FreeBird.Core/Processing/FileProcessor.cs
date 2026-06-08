using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
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
                var sourceStem = Path.GetFileNameWithoutExtension(sourcePath);
                var quarantinedPath = QuarantineFile(stagingPath, failedDir, $"{sourceStem}.bin", "Unknown format");
                return new ScanResult(
                    sourcePath,
                    ScanOutcome.UnknownFormat,
                    Format: AudioFormat.Unknown,
                    OutputPath: quarantinedPath,
                    Reason: "Unknown format");
            }

            // Step 5: integrity
            var integrity = await _integrity.CheckAsync(stagingPath, format, options.Integrity, cancellationToken)
                .ConfigureAwait(false);

            // Step 6: integrity failed -> quarantine
            if (!integrity.Ok)
            {
                var outputName = _naming.GetOutputFileName(sourcePath, format);
                var quarantinedPath = QuarantineFile(stagingPath, failedDir, outputName, integrity.Reason ?? "Integrity failed");
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

    private static string QuarantineFile(string stagingPath, string failedDir, string fileName, string reason)
    {
        Directory.CreateDirectory(failedDir);
        var dest = Path.Combine(failedDir, fileName);
        // If a previous quarantine of the same name exists, overwrite (latest failure wins)
        if (File.Exists(dest)) { File.Delete(dest); }
        File.Move(stagingPath, dest);

        var sidecarPath = dest + ".txt";
        File.WriteAllText(sidecarPath, reason);
        return dest;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); } }
        catch { /* best-effort */ }
    }
}
