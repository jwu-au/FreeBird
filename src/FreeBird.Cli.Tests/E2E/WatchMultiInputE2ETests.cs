using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// v3.4 T19 — End-to-end multi-input watch.
///
/// <c>fb watch dir1 dir2</c> spins up one <c>WatchTask</c> per input directory under
/// a single <c>WatchSupervisor</c>. The supervisor fans out, runs each task's
/// orchestrator concurrently, and on cancellation drains every task before returning.
///
/// Unlike <c>WatchRunnerMultiInputTests</c> (which mocks the orchestrator to test the
/// supervisor wiring), these tests drive the REAL <c>WatchOrchestrator</c> +
/// <c>FileProcessor</c> through the production container. That proves the v3.4
/// supervisor path actually decodes files end-to-end from multiple inputs into the
/// shared output dir, not just that the wiring compiles.
///
/// Patterned on <see cref="WatchInfiniteRetryRegressionTests"/>: drop a real .uc
/// fixture, kick off the runner with a short poll interval, poll for the expected
/// output, then cancel.
/// </summary>
[Collection("ConsoleRedirect")]
public class WatchMultiInputE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _outputDir;
    private readonly StemNamerTestOverride _namerOverride = new();

    public WatchMultiInputE2ETests()
    {
        _tempDir = WatchE2EHelpers.CreateTempPath("watch-multi");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        _namerOverride.Dispose();
        WatchE2EHelpers.CleanupDir(_tempDir);
    }

    private string MakeInputDir(string name)
    {
        var p = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static WatchOptions MakeOpts(string[] inputs, string outputDir) => new()
    {
        InputDirs = inputs,
        OutputDir = outputDir,
        Integrity = IntegrityLevel.Off, // skip flac probe in test env
        Concurrency = 2,
        Collision = CollisionPolicy.Overwrite,
        PollInterval = TimeSpan.FromMilliseconds(500),
        StabilityChecks = 1,
        MinFileSize = 0L,
        SkipInitialScan = false, // process pre-existing files on first sweep
        NoLogFile = true,
        Verbose = false,
        Quiet = true,
        Offline = true, // suppress network metadata calls
    };

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Watch_TwoValidInputDirs_BothFilesDecoded_IntoSharedOutput()
    {
        // Drop one real .uc fixture in each input dir BEFORE starting the watcher
        // so the initial sweep picks them up on its first poll cycle.
        var in1 = MakeInputDir("in1");
        var in2 = MakeInputDir("in2");
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, in1, "111-from-watch1.uc");
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, in2, "222-from-watch2.uc");

        var expected1 = Path.Combine(_outputDir, "111-from-watch1.mp3");
        var expected2 = Path.Combine(_outputDir, "222-from-watch2.mp3");

        var opts = MakeOpts(new[] { in1, in2 }, _outputDir);

        using var cts = new CancellationTokenSource();
        var runTask = WatchE2EHelpers.StartWatchAsync(opts, cts.Token);

        // Both files should appear within ~5s (initial sweep at +0, both decode together).
        var seen1 = await WatchE2EHelpers.WaitForFileAsync(expected1, TimeSpan.FromSeconds(5));
        var seen2 = await WatchE2EHelpers.WaitForFileAsync(expected2, TimeSpan.FromSeconds(5));

        seen1.Should().BeTrue($"watch must decode the file in dir1 → {expected1}");
        seen2.Should().BeTrue($"watch must decode the file in dir2 → {expected2}");

        cts.Cancel();
        // The runner should drain cleanly — no hang on Ctrl-C.
        var exit = await runTask;

        exit.Should().BeOneOf(
            new[] { WatchRunner.ExitOk, WatchRunner.ExitCancelled },
            "graceful cancel may report Ok (drained before signal mattered) or Cancelled (130)");
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Watch_OneValidOneInvalid_OnlyValidDirIsProcessed()
    {
        // One real input dir with a fixture, one nonexistent path. The supervisor
        // logs the invalid one as born-DEAD and runs only the valid task.
        var valid = MakeInputDir("valid");
        var invalid = Path.Combine(_tempDir, "ghost-dir-does-not-exist");

        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, valid, "333-only-valid.uc");

        var expected = Path.Combine(_outputDir, "333-only-valid.mp3");

        var opts = MakeOpts(new[] { valid, invalid }, _outputDir);

        using var cts = new CancellationTokenSource();
        var runTask = WatchE2EHelpers.StartWatchAsync(opts, cts.Token);

        var seen = await WatchE2EHelpers.WaitForFileAsync(expected, TimeSpan.FromSeconds(5));
        seen.Should().BeTrue(
            "the valid input dir must still be watched + processed despite a born-DEAD sibling");

        cts.Cancel();
        var exit = await runTask;

        exit.Should().BeOneOf(new[] { WatchRunner.ExitOk, WatchRunner.ExitCancelled });
    }

    [Fact(Skip =
        "Demoting an ACTIVE WatchTask whose dir vanished requires triggering the HealthProbe " +
        "tick mid-run. The probe interval is hard-coded to 5 minutes in production (not CLI-" +
        "exposed in v3.4); a clean E2E would need either a --health-probe-interval flag or a " +
        "TimeProvider hook surfaced through WatchRunner. Both are out of scope for v3.4 — " +
        "tracked as v4 backlog. Supervisor + HealthProbe demote/resurrect behavior IS covered " +
        "by FreeBird.Core.Tests.Watch.HealthProbeTests at the unit level.")]
    [Trait("Category", "E2E")]
    public Task Watch_DirectoryVanishes_HealthProbeDemotesTask()
    {
        // Intentionally skipped — see Skip reason above.
        return Task.CompletedTask;
    }
}
