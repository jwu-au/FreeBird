using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using Serilog.Events;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// T18 — E2E: when an <see cref="IFileProcessor"/> takes long enough that the polling
/// timer fires multiple times while the cycle lock is still held, the
/// <c>WatchOrchestrator</c> MUST emit its debounced skip-log sequence:
///   * 1st skip → WARN  ("Previous cycle still running, skipping this poll")
///   * 2nd-5th skip → DEBUG ("Skipping poll (cycle still running, consecutive=N)")
///   * 6th skip → WARN ("Long-running cycle detected ...")
///   * 7th+ skip → silent
///
/// Strategy:
///   * Install a custom <see cref="InMemoryLogSink"/> via
///     <see cref="WatchRunner.AdditionalLogSinks"/> so we can read the events emitted
///     by the runner-owned Serilog logger.
///   * Install a slow <see cref="IFileProcessor"/> via
///     <see cref="WatchRunner.AdditionalContainerSetup"/> that sleeps long enough
///     (8s) to span ~7 polling ticks at 1s intervals.
///   * Drop ONE real .uc fixture in the input dir so the first cycle has work to do.
///   * Run the watcher for ~9s, then cancel; assert on the captured log events.
///
/// Marked <c>Category=E2E</c> — see <see cref="WatchE2EHelpers"/> for rationale.
/// </summary>
[Collection("GlobalStaticState")]
public class CycleLockE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public CycleLockE2ETests()
    {
        _tempDir = WatchE2EHelpers.CreateTempPath("cycle-lock");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        // Always clear the static hooks so other tests don't see them.
        WatchRunner.AdditionalLogSinks.Clear();
        WatchRunner.AdditionalContainerSetup = null;
        WatchE2EHelpers.CleanupDir(_tempDir);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task SlowProcessor_HoldsCycleLock_EmitsWarnDebugDebounceThenWarnHint()
    {
        // 1) Drop one real .uc fixture so the cycle has actual work to perform.
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, _inputDir, "long-cycle.uc");

        // 2) Install in-memory sink. Verbose=true on the options (below) raises the
        //    global Serilog minimum to Debug so the consecutive=N DEBUG events are
        //    actually emitted to our sink.
        var sink = new InMemoryLogSink();
        WatchRunner.AdditionalLogSinks.Add(sink);

        // 3) Install slow processor. 8 seconds is enough for ~7 polling ticks at 1s
        //    interval to occur while the cycle lock is held.
        WatchRunner.AdditionalContainerSetup = builder =>
        {
            builder.RegisterInstance(new SlowFileProcessor { Delay = TimeSpan.FromSeconds(8) })
                .As<IFileProcessor>()
                .SingleInstance();
        };

        // 4) Launch the runner. Verbose=true so DEBUG events propagate; skip-initial-scan
        //    so the first cycle is the polling-driven one (and we observe its skips).
        //    Don't use WatchE2EHelpers.MakeOptions here — it hard-codes Quiet=true, which
        //    would suppress the DEBUG events we need to assert on.
        var opts = new WatchOptions
        {
            InputDirs = new[] { _inputDir },
            OutputDir = _outputDir,
            Integrity = FreeBird.Core.Models.IntegrityLevel.Off,
            Concurrency = 2,
            Collision = FreeBird.Core.Models.CollisionPolicy.Overwrite,
            PollInterval = TimeSpan.FromSeconds(1),
            StabilityChecks = 1,
            MinFileSize = 0L,
            SkipInitialScan = true,
            NoLogFile = true,
            Verbose = true,
            Quiet = false,
        };

        using var cts = new CancellationTokenSource();
        var runTask = WatchE2EHelpers.StartWatchAsync(opts, cts.Token);

        // 5) Wait ~9.5s — first poll at +1s starts the cycle (held for 8s), so the
        //    next 7 polls at +2..+8 all see the lock held. That gives us:
        //       skip #1 at ~+2s  -> WARN
        //       skip #2..#5 at +3..+6 -> DEBUG
        //       skip #6 at +7s -> WARN-hint
        //       skip #7 at +8s -> silent
        //    The cycle finishes around +9s. Cancel just after to drain.
        await Task.Delay(TimeSpan.FromMilliseconds(9500));
        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected */ }

        // 6) Filter for orchestrator events only (the runner itself emits unrelated
        //    Information events). WatchOrchestrator doesn't set a SourceContext, so
        //    filter by message-template content instead.
        var allEvents = sink.Events;

        var firstWarn = allEvents
            .Where(e => e.Level == LogEventLevel.Warning
                     && e.MessageTemplate.Text.Contains("Previous cycle still running"))
            .ToList();
        var debugSkips = allEvents
            .Where(e => e.Level == LogEventLevel.Debug
                     && e.MessageTemplate.Text.Contains("Skipping poll")
                     && e.MessageTemplate.Text.Contains("consecutive="))
            .ToList();
        var longRunningHint = allEvents
            .Where(e => e.Level == LogEventLevel.Warning
                     && e.MessageTemplate.Text.Contains("Long-running cycle detected"))
            .ToList();

        firstWarn.Should().HaveCount(1,
            "exactly the 1st skipped poll must log the WARN 'Previous cycle still running'");
        debugSkips.Should().HaveCount(4,
            "skips #2-#5 must log DEBUG 'Skipping poll ... consecutive=N'");
        longRunningHint.Should().HaveCount(1,
            "exactly the 6th skipped poll must log the WARN 'Long-running cycle detected' hint");

        // Bonus: the 4 DEBUG events should carry consecutive=2..5 in that order.
        var consecutiveValues = debugSkips
            .Select(e => e.Properties.TryGetValue("Count", out var v) ? v.ToString() : null)
            .Where(s => s is not null)
            .ToList();
        consecutiveValues.Should().BeEquivalentTo(
            new[] { "2", "3", "4", "5" },
            opts => opts.WithStrictOrdering(),
            "DEBUG skip events should carry consecutive=2..5 as their Count property");
    }

    /// <summary>
    /// Stand-in <see cref="IFileProcessor"/> that simply sleeps for <see cref="Delay"/>
    /// and returns an Ok result. We don't care about the actual decode outcome — the test
    /// only asserts on the cycle-lock log sequence emitted while we're sleeping.
    /// </summary>
    internal sealed class SlowFileProcessor : IFileProcessor
    {
        public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(7);

        public async Task<ScanResult> ProcessAsync(
            string sourcePath,
            ScanOptions options,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
            return new ScanResult(
                SourcePath: sourcePath,
                Outcome: ScanOutcome.Ok,
                Format: AudioFormat.Mp3,
                OutputPath: sourcePath + ".mp3");
        }
    }
}
