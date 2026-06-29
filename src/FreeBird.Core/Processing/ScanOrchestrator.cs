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
/// Top-level scan driver: enumerates .uc/.uc!/.ncm files in a directory, dispatches each
/// through the <see cref="IFileProcessorRouter"/>-selected processor with
/// Parallel.ForEachAsync, aggregates results into a ScanSummary.
/// </summary>
public sealed class ScanOrchestrator : IScanOrchestrator
{
    private readonly IFileProcessorRouter _router;
    private readonly ILogger _logger;
    private readonly IResolvedMarkerGate _resolvedMarkerGate;

    public ScanOrchestrator(IFileProcessorRouter router, ILogger logger, IResolvedMarkerGate resolvedMarkerGate)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resolvedMarkerGate = resolvedMarkerGate ?? throw new ArgumentNullException(nameof(resolvedMarkerGate));
    }

    public async Task<ScanSummary> RunAsync(ScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.InputDirectories);

        // v3.4 T13: FAIL-FAST — validate every input directory exists BEFORE
        // starting any work. Differs from `fb watch` born-DEAD semantics by design
        // (see design-spec §2.5).
        foreach (var inputDir in options.InputDirectories)
        {
            if (!Directory.Exists(inputDir))
            {
                throw new DirectoryNotFoundException($"Input directory not found: {inputDir}");
            }
        }

        var sw = Stopwatch.StartNew();
        _logger.Information("Scan starting. Inputs={Count}, Output={Output}, Integrity={Level}, Concurrency={Concurrency}",
            options.InputDirectories.Count, options.OutputDirectory, options.Integrity, options.Concurrency);

        // Enumerate ALL input dirs into a single flat list; the parallel loop
        // below then dispatches across the union.
        var files = options.InputDirectories.SelectMany(EnumerateSources).ToArray();
        _logger.Information("Found {Count} candidate file(s) across {Inputs} input dir(s)", files.Length, options.InputDirectories.Count);

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
                // T3: consult the shared marker gate BEFORE dispatching to the
                // processor. An already-resolved file (valid+fresh marker, output
                // present, retry-backoff not due) is skipped here so its metadata
                // is never re-requested from NetEase. This also fixes the watch
                // initial sweep transitively (it flows through ScanOrchestrator).
                var gateDecision = _resolvedMarkerGate.TryShortCircuit(file, options.OutputDirectory, options.NamingTemplate, ct);
                if (gateDecision is not null)
                {
                    Interlocked.Increment(ref skipped);
                    _logger.Information("SKIP {Source} (already resolved; marker hit)", file);
                    return; // no ProcessAsync => no metadata API call
                }

                var result = await _router.Select(file).ProcessAsync(file, options, ct).ConfigureAwait(false);
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
        // Non-recursive (TopDirectoryOnly). Match .uc, .uc! and .ncm.
        foreach (var path in Directory.EnumerateFiles(inputDir, "*.uc", SearchOption.TopDirectoryOnly))
        {
            if (!IsHidden(path)) { yield return path; }
        }
        foreach (var path in Directory.EnumerateFiles(inputDir, "*.uc!", SearchOption.TopDirectoryOnly))
        {
            if (!IsHidden(path)) { yield return path; }
        }
        // Task 15: .ncm is the encrypted NetEase container; the router dispatches these
        // to NcmFileProcessor while .uc/.uc! continue to the default FileProcessor.
        foreach (var path in Directory.EnumerateFiles(inputDir, "*.ncm", SearchOption.TopDirectoryOnly))
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
