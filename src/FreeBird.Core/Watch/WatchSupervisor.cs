using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Models;
using Serilog;

namespace FreeBird.Core.Watch;

/// <summary>
/// Orchestrates multiple <see cref="WatchTask"/> instances (one per input directory).
///
/// T08 scope: input parsing + validation + INITIALIZING fan-out (construction only).
/// T09 will add the RunAsync lifecycle (start tasks, integrate HealthProbe, propagate
/// cancellation, and join on shutdown).
///
/// Path canonicalization rules:
///   - <see cref="Path.GetFullPath(string)"/> + trailing-separator trim.
///   - Dedupe comparer is case-insensitive on Windows and macOS (HFS+/APFS default to
///     case-insensitive), case-sensitive on Linux. Selected via
///     <see cref="RuntimeInformation.IsOSPlatform"/>.
///   - Empty / whitespace / unparseable entries are routed to the invalid list and logged
///     at WARN. Duplicates are dropped silently (logged at WARN) but NOT counted as invalid.
/// </summary>
public sealed class WatchSupervisor
{
    private readonly Func<WatchInput, WatchTask> _taskFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _log;

    public WatchSupervisor(
        Func<WatchInput, WatchTask> taskFactory,
        TimeProvider timeProvider,
        ILogger log)
    {
        _taskFactory = taskFactory ?? throw new ArgumentNullException(nameof(taskFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Parse a raw list of input directory strings into validated <see cref="WatchInput"/>
    /// instances. Dedupes by canonical path using an OS-appropriate comparer.
    /// </summary>
    /// <returns>
    /// (Valid, Invalid). Invalid contains raw input strings that could not be canonicalized
    /// (empty/whitespace/null/throwing path). Duplicates are dropped silently and do NOT
    /// appear in either list beyond the first occurrence.
    /// </returns>
    public (IReadOnlyList<WatchInput> Valid, IReadOnlyList<string> Invalid) ParseAndValidateInputs(
        IReadOnlyList<string> inputPaths)
    {
        if (inputPaths is null) { throw new ArgumentNullException(nameof(inputPaths)); }

        var valid = new List<WatchInput>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(GetPathComparer());

        foreach (var raw in inputPaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                _log.Warning("watch input skipped: empty path");
                invalid.Add(raw ?? string.Empty);
                continue;
            }

            string canonical;
            try
            {
                canonical = Path.GetFullPath(raw).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (canonical.Length == 0)
                {
                    canonical = Path.DirectorySeparatorChar.ToString();
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "watch input skipped: cannot canonicalize {Path}", raw);
                invalid.Add(raw);
                continue;
            }

            if (!seen.Add(canonical))
            {
                _log.Warning("watch input skipped: duplicate of already-listed input ({Path})", raw);
                continue;
            }

            valid.Add(WatchInput.FromPath(canonical));
        }

        return (valid, invalid);
    }

    /// <summary>
    /// Construct one <see cref="WatchTask"/> per valid input via the injected factory.
    /// The WatchTask constructor itself decides INITIALIZING vs born-DEAD based on whether
    /// the directory exists (see <see cref="WatchTask"/>). Order is preserved.
    /// </summary>
    public IReadOnlyList<WatchTask> CreateTasks(IReadOnlyList<WatchInput> validInputs)
    {
        if (validInputs is null) { throw new ArgumentNullException(nameof(validInputs)); }

        var tasks = new List<WatchTask>(validInputs.Count);
        foreach (var input in validInputs)
        {
            var task = _taskFactory(input);
            tasks.Add(task);
            _log.Information(
                "[watch={Base}] task created in state {State}",
                input.BaseName, task.State);
        }
        return tasks;
    }

    private static StringComparer GetPathComparer() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// Run all <paramref name="tasks"/> concurrently until they complete naturally or
    /// <paramref name="externalCt"/> is cancelled. Drains gracefully on cancellation:
    /// every task receives <see cref="WatchTask.Cancel"/>, all are awaited via
    /// <see cref="Task.WhenAll(System.Threading.Tasks.Task[])"/> (no <c>WhenAny</c> race),
    /// and per-task <see cref="ScanSummary"/> results are summed into a single aggregate.
    ///
    /// Failure isolation: an exception escaping a single task's <c>RunAsync</c> is logged at
    /// WARN and the task contributes an empty summary; sibling tasks continue and are awaited.
    /// Born-DEAD tasks already short-circuit to an empty summary inside <see cref="WatchTask.RunAsync"/>.
    ///
    /// Per-task <see cref="WatchOptions"/>: <paramref name="optionsTemplate"/> is cloned per task
    /// with <c>InputDirs</c> overridden to <c>[ task.Input.Path ]</c>. All other knobs (output dir,
    /// concurrency, integrity, etc.) are inherited from the template.
    ///
    /// Scope (T09): lifecycle only. Health-probe demotion (T11) and per-task restart on non-fatal
    /// crash (caller's responsibility) are out of scope here.
    /// </summary>
    public async Task<ScanSummary> RunAsync(
        IReadOnlyList<WatchTask> tasks,
        WatchOptions optionsTemplate,
        CancellationToken externalCt)
    {
        if (tasks is null) { throw new ArgumentNullException(nameof(tasks)); }
        if (optionsTemplate is null) { throw new ArgumentNullException(nameof(optionsTemplate)); }

        if (tasks.Count == 0)
        {
            _log.Warning("watch supervisor: no tasks to run; returning empty summary");
            return EmptySummary();
        }

        _log.Information("watch supervisor: starting {Count} tasks", tasks.Count);

        var perTaskRuns = new List<Task<ScanSummary>>(tasks.Count);
        foreach (var task in tasks)
        {
            var perTaskOptions = optionsTemplate with { InputDirs = new[] { task.Input.Path } };
            perTaskRuns.Add(RunOneTaskIsolated(task, perTaskOptions, externalCt));
        }

        // External cancellation: signal every task to drain. Registration is disposed on exit
        // so we don't leak a callback after the supervisor's RunAsync returns.
        using var externalReg = externalCt.Register(() =>
        {
            _log.Information("watch supervisor: external cancellation; signaling all tasks to drain");
            foreach (var t in tasks) { t.Cancel(); }
        });

        var summaries = await Task.WhenAll(perTaskRuns).ConfigureAwait(false);
        var total = AggregateSummaries(summaries);
        _log.Information(
            "watch supervisor: all {Count} tasks completed; aggregate Ok={Ok} Failed={Failed}",
            tasks.Count, total.Ok, total.IntegrityFailed + total.Errors);
        return total;
    }

    private async Task<ScanSummary> RunOneTaskIsolated(
        WatchTask task,
        WatchOptions options,
        CancellationToken ct)
    {
        try
        {
            return await task.RunAsync(options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on shutdown drain — contribute empty.
            return EmptySummary();
        }
        catch (Exception ex)
        {
            // Non-fatal crash escaped WatchTask.RunAsync (rethrown by design for the supervisor
            // to handle). T09 isolates from siblings only — restart policy is out of scope.
            _log.Warning(ex, "[watch={Base}] task crashed; isolated from other tasks", task.Input.BaseName);
            return EmptySummary();
        }
    }

    private static ScanSummary AggregateSummaries(IReadOnlyList<ScanSummary> perTask)
    {
        int processed = 0, ok = 0, skipped = 0, unknown = 0, integrityFailed = 0, errors = 0;
        TimeSpan duration = TimeSpan.Zero;
        foreach (var s in perTask)
        {
            processed += s.Processed;
            ok += s.Ok;
            skipped += s.Skipped;
            unknown += s.UnknownFormat;
            integrityFailed += s.IntegrityFailed;
            errors += s.Errors;
            duration += s.Duration;
        }
        return new ScanSummary(processed, ok, skipped, unknown, integrityFailed, errors, duration);
    }

    private static ScanSummary EmptySummary() =>
        new ScanSummary(
            Processed: 0,
            Ok: 0,
            Skipped: 0,
            UnknownFormat: 0,
            IntegrityFailed: 0,
            Errors: 0,
            Duration: TimeSpan.Zero);
}
