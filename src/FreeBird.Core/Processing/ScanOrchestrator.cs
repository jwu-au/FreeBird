using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using Serilog;

namespace FreeBird.Core.Processing;

/// <summary>
/// Top-level scan driver: enumerates .uc/.uc! files in a directory, dispatches them through
/// IFileProcessor with Parallel.ForEachAsync, aggregates results into a ScanSummary.
/// </summary>
public sealed class ScanOrchestrator : IScanOrchestrator
{
    private readonly IFileProcessor _processor;
    private readonly ILogger _logger;

    public ScanOrchestrator(IFileProcessor processor, ILogger logger)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ScanSummary> RunAsync(ScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Directory.Exists(options.InputDirectory))
        {
            throw new DirectoryNotFoundException($"Input directory not found: {options.InputDirectory}");
        }

        var sw = Stopwatch.StartNew();
        _logger.Information("Scan starting. Input={Input}, Output={Output}, Integrity={Level}, Concurrency={Concurrency}",
            options.InputDirectory, options.OutputDirectory, options.Integrity, options.Concurrency);

        var files = EnumerateSources(options.InputDirectory).ToArray();
        _logger.Information("Found {Count} candidate file(s) to process", files.Length);

        // Counters (thread-safe)
        int ok = 0, skipped = 0, unknown = 0, integrityFailed = 0, errors = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(files, parallelOptions, async (file, ct) =>
        {
            try
            {
                var result = await _processor.ProcessAsync(file, options, ct).ConfigureAwait(false);
                LogResult(result);
                IncrementCounter(result.Outcome,
                    ref ok, ref skipped, ref unknown, ref integrityFailed, ref errors);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errors);
                _logger.Error(ex, "Unhandled error processing {File}", file);
            }
        }).ConfigureAwait(false);

        sw.Stop();
        var summary = new ScanSummary(
            Processed: files.Length,
            Ok: ok,
            Skipped: skipped,
            UnknownFormat: unknown,
            IntegrityFailed: integrityFailed,
            Errors: errors,
            Duration: sw.Elapsed);

        _logger.Information(
            "Scan complete. Processed={Processed}, Ok={Ok}, Skipped={Skipped}, Unknown={Unknown}, IntegrityFailed={IntegrityFailed}, Errors={Errors}, Duration={Duration}",
            summary.Processed, summary.Ok, summary.Skipped, summary.UnknownFormat, summary.IntegrityFailed, summary.Errors, summary.Duration);

        return summary;
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateSources(string inputDir)
    {
        // Non-recursive (TopDirectoryOnly). Match both .uc and .uc!
        foreach (var path in Directory.EnumerateFiles(inputDir, "*.uc", SearchOption.TopDirectoryOnly))
        {
            if (!IsHidden(path)) { yield return path; }
        }
        foreach (var path in Directory.EnumerateFiles(inputDir, "*.uc!", SearchOption.TopDirectoryOnly))
        {
            if (!IsHidden(path)) { yield return path; }
        }
    }

    private static bool IsHidden(string filePath)
    {
        var name = Path.GetFileName(filePath);
        return name.StartsWith('.');
    }

    private void LogResult(ScanResult r)
    {
        switch (r.Outcome)
        {
            case ScanOutcome.Ok:
                _logger.Information("OK   {Source} -> {Output} ({Format}, integrity={Level})",
                    r.SourcePath, r.OutputPath, r.Format, r.Integrity?.LevelApplied);
                break;
            case ScanOutcome.Skipped:
                _logger.Information("SKIP {Source} -> {Output} (already exists)", r.SourcePath, r.OutputPath);
                break;
            case ScanOutcome.UnknownFormat:
                _logger.Warning("UNKN {Source} -> quarantined ({Reason})", r.SourcePath, r.Reason);
                break;
            case ScanOutcome.IntegrityFailed:
                _logger.Warning("FAIL {Source} -> quarantined ({Reason})", r.SourcePath, r.Reason);
                break;
            case ScanOutcome.Error:
                _logger.Error("ERR  {Source} ({Reason})", r.SourcePath, r.Reason);
                break;
        }
    }

    private static void IncrementCounter(ScanOutcome outcome,
        ref int ok, ref int skipped, ref int unknown, ref int integrityFailed, ref int errors)
    {
        switch (outcome)
        {
            case ScanOutcome.Ok: Interlocked.Increment(ref ok); break;
            case ScanOutcome.Skipped: Interlocked.Increment(ref skipped); break;
            case ScanOutcome.UnknownFormat: Interlocked.Increment(ref unknown); break;
            case ScanOutcome.IntegrityFailed: Interlocked.Increment(ref integrityFailed); break;
            case ScanOutcome.Error: Interlocked.Increment(ref errors); break;
        }
    }
}
