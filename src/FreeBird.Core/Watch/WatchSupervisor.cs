using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
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
}
