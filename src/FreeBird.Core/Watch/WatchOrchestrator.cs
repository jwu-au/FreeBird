using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using Serilog;

namespace FreeBird.Core.Watch;

/// <summary>
/// The <c>fb watch</c> top-level orchestrator (v2 T08).
///
/// Lifecycle:
/// <list type="number">
///   <item><description>Optional initial sweep via <see cref="IScanOrchestrator"/> (unless <see cref="WatchOptions.SkipInitialScan"/>).</description></item>
///   <item><description>Polling loop on <see cref="WatchOptions.PollInterval"/> (using injected <see cref="TimeProvider"/> so it's testable with <c>FakeTimeProvider</c>).</description></item>
///   <item><description>Each cycle: enumerate <c>.uc</c>/<c>.uc!</c> in <see cref="WatchOptions.InputDirs"/>, ask <see cref="ICompletionDetector"/> per file, ask <see cref="ISkipDecider"/> per stable file, then dispatch survivors through <see cref="IFileProcessor"/> via <see cref="Parallel.ForEachAsync(System.Collections.Generic.IEnumerable{string}, ParallelOptions, Func{string, CancellationToken, ValueTask})"/>.</description></item>
///   <item><description>Cycle lock prevents overlapping cycles; consecutive-skip counter drives debounced WARN→DEBUG→WARN-hint log levels.</description></item>
///   <item><description>Graceful <c>cancellationToken</c> stops scheduling new cycles; hard <c>hardAbortToken</c> is forwarded to <see cref="IFileProcessor"/> so in-flight work can be torn down on second SIGINT.</description></item>
/// </list>
/// </summary>
public sealed class WatchOrchestrator : IWatchOrchestrator
{
    private readonly IScanOrchestrator _scanOrchestrator;
    private readonly ICompletionDetector _completionDetector;
    private readonly ISkipDecider _skipDecider;
    private readonly IFileProcessor _fileProcessor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _cycleLock = new(1, 1);
    private int _consecutiveSkips;

    public WatchOrchestrator(
        IScanOrchestrator scanOrchestrator,
        ICompletionDetector completionDetector,
        ISkipDecider skipDecider,
        IFileProcessor fileProcessor,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _scanOrchestrator = scanOrchestrator ?? throw new ArgumentNullException(nameof(scanOrchestrator));
        _completionDetector = completionDetector ?? throw new ArgumentNullException(nameof(completionDetector));
        _skipDecider = skipDecider ?? throw new ArgumentNullException(nameof(skipDecider));
        _fileProcessor = fileProcessor ?? throw new ArgumentNullException(nameof(fileProcessor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ScanSummary> RunAsync(
        WatchOptions options,
        CancellationToken cancellationToken = default,
        CancellationToken hardAbortToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.InputDirs is null || options.InputDirs.Count != 1)
        {
            // v3.4: WatchOrchestrator handles a single input dir. Multi-input fan-out
            // happens one level up in WatchSupervisor (T09/T10), which spawns one
            // WatchOrchestrator instance per input dir.
            throw new ArgumentException(
                "WatchOrchestrator requires exactly one entry in WatchOptions.InputDirs; " +
                "use WatchSupervisor for multi-input watches.",
                nameof(options));
        }
        var inputDir = options.InputDirs[0];

        var sw = Stopwatch.StartNew();
        int ok = 0, skipped = 0, unknown = 0, integrityFailed = 0, errors = 0;
        int totalProcessed = 0;

        _logger.Information(
            "Watch starting. Input={Input}, Output={Output}, PollInterval={Poll}, Concurrency={Concurrency}, StabilityChecks={Checks}, MinFileSizeBytes={Min}, SkipInitialScan={Skip}",
            inputDir, options.OutputDir, options.PollInterval, options.Concurrency,
            options.StabilityChecks, options.MinFileSizeBytes, options.SkipInitialScan);

        // --- 1. Initial sweep (delegated to v1's IScanOrchestrator) ---
        if (!options.SkipInitialScan)
        {
            try
            {
                _logger.Information("Watch: running initial sweep");
                var initialSummary = await _scanOrchestrator.RunAsync(ToScanOptions(options), cancellationToken).ConfigureAwait(false);
                ok += initialSummary.Ok;
                skipped += initialSummary.Skipped;
                unknown += initialSummary.UnknownFormat;
                integrityFailed += initialSummary.IntegrityFailed;
                errors += initialSummary.Errors;
                totalProcessed += initialSummary.Processed;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                return BuildSummary(totalProcessed, ok, skipped, unknown, integrityFailed, errors, sw.Elapsed);
            }
        }

        // --- 2. Polling loop ---
        // Timer ticks must fire INDEPENDENTLY of cycle execution: when a tick fires while
        // the previous cycle is still in flight, we log a skip (not wait). So the cycle
        // runs as a fire-and-forget Task; the polling loop only owns the timer + lock.
        Task? activeCycle = null;
        Action<CycleCounters> accumulator = (delta) =>
        {
            Interlocked.Add(ref ok, delta.Ok);
            Interlocked.Add(ref skipped, delta.Skipped);
            Interlocked.Add(ref unknown, delta.Unknown);
            Interlocked.Add(ref integrityFailed, delta.IntegrityFailed);
            Interlocked.Add(ref errors, delta.Errors);
            Interlocked.Add(ref totalProcessed, delta.Processed);
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // Try to acquire the cycle lock without blocking; if previous cycle still running, debounce.
            if (!await _cycleLock.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                LogSkippedPoll();
                continue;
            }

            _consecutiveSkips = 0;
            // Fire-and-forget the cycle; the lock is released by the cycle task itself.
            activeCycle = Task.Run(async () =>
            {
                try
                {
                    await RunCycleAsync(options, accumulator, cancellationToken, hardAbortToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Swallow; graceful cancellation OR hard abort. The accumulated summary
                    // is what we return.
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Watch: cycle failed with unexpected exception");
                }
                finally
                {
                    _cycleLock.Release();
                }
            });
        }

        // Drain: wait for any in-flight cycle to finish (graceful drain on cancellation).
        if (activeCycle is not null)
        {
            try
            {
                await activeCycle.ConfigureAwait(false);
            }
            catch
            {
                // already logged in the cycle's own catch
            }
        }

        sw.Stop();
        var summary = BuildSummary(totalProcessed, ok, skipped, unknown, integrityFailed, errors, sw.Elapsed);
        _logger.Information(
            "Watch complete. Processed={Processed}, Ok={Ok}, Skipped={Skipped}, Unknown={Unknown}, IntegrityFailed={IntegrityFailed}, Errors={Errors}, Duration={Duration}",
            summary.Processed, summary.Ok, summary.Skipped, summary.UnknownFormat,
            summary.IntegrityFailed, summary.Errors, summary.Duration);
        return summary;
    }

    /// <summary>
    /// Run a single polling cycle: enumerate, filter by stability + skip-decider, then process in parallel.
    /// </summary>
    private async Task RunCycleAsync(
        WatchOptions options,
        Action<CycleCounters> reporter,
        CancellationToken softCt,
        CancellationToken hardCt)
    {
        // v3.4: per-invariant in RunAsync, InputDirs has exactly one entry here.
        var inputDir = options.InputDirs[0];

        // Enumerate candidate files (non-recursive).
        string[] candidates;
        try
        {
            candidates = EnumerateSources(inputDir).ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            _logger.Warning("Watch: input directory missing: {Dir}", inputDir);
            return;
        }

        if (candidates.Length == 0)
        {
            return;
        }

        // Counters accumulate over BOTH the enumerate phase and the parallel-process phase.
        // The try/finally ensures that even if cancellation (or any other exception) aborts
        // either phase mid-flight, the partial counts that DID accumulate get reported into
        // the summary instead of being silently dropped.
        var counters = new CycleCounters();
        var readyToProcess = new List<string>();
        try
        {
            // For each candidate: detect stability, then ask the skip-decider.
            foreach (var path in candidates)
            {
                softCt.ThrowIfCancellationRequested();

                bool stable;
                try
                {
                    stable = await _completionDetector.IsStableAsync(path, options.StabilityChecks, softCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Watch: completion detector failed for {Path}; skipping this cycle", path);
                    continue;
                }

                if (!stable)
                {
                    _logger.Debug("Watch: {Path} not yet stable; will retry next cycle", path);
                    continue;
                }

                SkipDecision decision;
                try
                {
                    decision = await _skipDecider.DecideAsync(path, options, softCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Watch: skip-decider failed for {Path}; treating as Process", path);
                    decision = SkipDecision.Process();
                }

                if (!decision.ShouldProcess)
                {
                    _logger.Debug("Watch: skipping {Path} (reason={Reason}, detail={Detail})",
                        path, decision.Reason, decision.Detail);
                    _completionDetector.Forget(path);
                    continue;
                }

                readyToProcess.Add(path);
            }

            if (readyToProcess.Count == 0)
            {
                return;
            }

            counters.Processed = readyToProcess.Count;
            var scanOptions = ToScanOptions(options);
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
                CancellationToken = hardCt,
            };

            await Parallel.ForEachAsync(readyToProcess, parallelOptions, async (path, ct) =>
            {
                try
                {
                    var result = await _fileProcessor.ProcessAsync(path, scanOptions, ct).ConfigureAwait(false);
                    LogResult(result);
                    switch (result.Outcome)
                    {
                        case ScanOutcome.Ok: Interlocked.Increment(ref counters.Ok); break;
                        case ScanOutcome.Skipped: Interlocked.Increment(ref counters.Skipped); break;
                        case ScanOutcome.UnknownFormat: Interlocked.Increment(ref counters.Unknown); break;
                        case ScanOutcome.IntegrityFailed: Interlocked.Increment(ref counters.IntegrityFailed); break;
                        case ScanOutcome.Error: Interlocked.Increment(ref counters.Errors); break;
                    }
                    // Freshness reset: forget tracked state for files we just acted on, unless we deliberately
                    // chose to keep them around (only Skipped means "deferred to v1 collision policy" — even then
                    // the file may be replaced later by a new download, so forget is correct).
                    _completionDetector.Forget(path);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref counters.Errors);
                    _logger.Error(ex, "Watch: unhandled error processing {Path}", path);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            // Always flush partial counters to the summary, even on graceful/hard cancel.
            reporter(counters);
        }
    }

    /// <summary>
    /// Emit the debounced "previous cycle still running" log, ramping
    /// WARN → DEBUG (4×) → WARN (escalation hint) → silent.
    /// </summary>
    private void LogSkippedPoll()
    {
        var n = Interlocked.Increment(ref _consecutiveSkips);
        if (n == 1)
        {
            _logger.Warning("Previous cycle still running, skipping this poll");
        }
        else if (n <= 5)
        {
            _logger.Debug("Skipping poll (cycle still running, consecutive={Count})", n);
        }
        else if (n == 6)
        {
            _logger.Warning(
                "Long-running cycle detected (>=6 skipped polls); consider raising --poll-interval or --concurrency");
        }
        // n >= 7 → silent to avoid log spam
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

    private static IEnumerable<string> EnumerateSources(string inputDir)
    {
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

    /// <summary>
    /// Map watch options onto the v1 scan options used by IScanOrchestrator and IFileProcessor.
    /// </summary>
    private static ScanOptions ToScanOptions(WatchOptions w) => new(
        InputDirectories: new[] { w.InputDirs[0] },
        OutputDirectory: w.OutputDir,
        Integrity: w.Integrity,
        Concurrency: w.Concurrency,
        OnCollision: w.Collision);

    private static ScanSummary BuildSummary(int processed, int ok, int skipped, int unknown, int integrityFailed, int errors, TimeSpan duration)
        => new(processed, ok, skipped, unknown, integrityFailed, errors, duration);

    /// <summary>
    /// Mutable per-cycle counters; fields, not properties, so <see cref="Interlocked"/> can take refs.
    /// </summary>
    private sealed class CycleCounters
    {
        public int Processed;
        public int Ok;
        public int Skipped;
        public int Unknown;
        public int IntegrityFailed;
        public int Errors;
    }
}
