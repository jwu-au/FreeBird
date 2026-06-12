using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using Moq;
using Serilog;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T16 — Verifies WatchRunner uses the v3.4 supervisor path: parses multiple input
/// directories, creates one WatchTask per valid input, drops invalid inputs with a
/// WARN log, and exits cleanly when all tasks complete (or all inputs are invalid).
///
/// Uses <see cref="WatchRunner.OrchestratorFactoryOverride"/> so the underlying watch
/// loop is mocked — these tests exercise the supervisor/runner wiring only.
/// </summary>
[Collection("ConsoleRedirect")]
public class WatchRunnerMultiInputTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _outputDir;

    public WatchRunnerMultiInputTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-wrmi-{Guid.NewGuid():N}");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        WatchRunner.OrchestratorFactoryOverride = null;
        WatchRunner.CoordinatorFactoryOverride = null;
        WatchRunner.AdditionalContainerSetup = null;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string MakeInputDir(string name)
    {
        var p = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private WatchOptions MakeOpts(string[] inputs) => new()
    {
        InputDirs = inputs,
        OutputDir = _outputDir,
        Integrity = IntegrityLevel.Off,
        Concurrency = 2,
        Collision = CollisionPolicy.Skip,
        PollInterval = TimeSpan.FromSeconds(5),
        StabilityChecks = 2,
        MinFileSize = 1024,
        SkipInitialScan = true,
        NoLogFile = true,
    };

    private static ScanSummary EmptySummary() =>
        new(Processed: 0, Ok: 0, Skipped: 0, UnknownFormat: 0, IntegrityFailed: 0, Errors: 0, Duration: TimeSpan.Zero);

    [Fact]
    public async Task Run_AllInputDirsInvalid_ExitsOk()
    {
        // All-invalid case: validInputs.Count == 0 — supervisor has nothing to do.
        // Per spec §2.5 watch born-DEAD is allowed; this is a no-op success (WARN logged).
        var inputs = new[]
        {
            Path.Combine(_tempDir, "does-not-exist-1"),
            Path.Combine(_tempDir, "does-not-exist-2"),
        };

        // Orchestrator must NOT be invoked — there's no work to do.
        var orchInvocations = 0;
        WatchRunner.OrchestratorFactoryOverride = _ =>
        {
            Interlocked.Increment(ref orchInvocations);
            var mock = new Mock<IWatchOrchestrator>();
            mock.Setup(o => o.RunAsync(
                    It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(EmptySummary());
            return mock.Object;
        };

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(inputs), CancellationToken.None);

        exit.Should().Be(WatchRunner.ExitOk,
            because: "all-invalid inputs is a no-op success per born-DEAD spec");
        orchInvocations.Should().Be(0,
            because: "no valid inputs => no WatchTask => orchestrator factory must not fire");
    }

    [Fact]
    public async Task Run_TwoValidInputDirs_RunsSupervisorWithBothTasks()
    {
        var in1 = MakeInputDir("in1");
        var in2 = MakeInputDir("in2");

        // Track per-task orchestrator invocations + the InputDirs each one saw.
        var invocations = 0;
        var seenInputs = new System.Collections.Concurrent.ConcurrentBag<string>();

        WatchRunner.OrchestratorFactoryOverride = _ =>
        {
            var mock = new Mock<IWatchOrchestrator>();
            mock.Setup(o => o.RunAsync(
                    It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<CancellationToken>()))
                .Callback<FreeBird.Core.Models.WatchOptions, CancellationToken, CancellationToken>((opts, _, _) =>
                {
                    Interlocked.Increment(ref invocations);
                    seenInputs.Add(opts.InputDirs[0]);
                })
                .ReturnsAsync(EmptySummary());
            return mock.Object;
        };

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(new[] { in1, in2 }), CancellationToken.None);

        exit.Should().Be(WatchRunner.ExitOk);
        invocations.Should().Be(2,
            because: "supervisor must create one WatchTask per valid input and run each orchestrator");
        seenInputs.Should().BeEquivalentTo(new[] { in1, in2 },
            because: "each task gets a per-task WatchOptions with InputDirs = [task.Input.Path]");
    }

    [Fact]
    public async Task Run_OneValidOneInvalid_RunsOnlyValid()
    {
        var valid = MakeInputDir("valid");
        var invalid = Path.Combine(_tempDir, "missing");

        var invocations = 0;
        var seenInputs = new System.Collections.Concurrent.ConcurrentBag<string>();

        WatchRunner.OrchestratorFactoryOverride = _ =>
        {
            var mock = new Mock<IWatchOrchestrator>();
            mock.Setup(o => o.RunAsync(
                    It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<CancellationToken>()))
                .Callback<FreeBird.Core.Models.WatchOptions, CancellationToken, CancellationToken>((opts, _, _) =>
                {
                    Interlocked.Increment(ref invocations);
                    seenInputs.Add(opts.InputDirs[0]);
                })
                .ReturnsAsync(EmptySummary());
            return mock.Object;
        };

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(new[] { valid, invalid }), CancellationToken.None);

        exit.Should().Be(WatchRunner.ExitOk);
        invocations.Should().Be(1,
            because: "invalid dir is dropped from valid list — only one WatchTask is created");
        seenInputs.Should().BeEquivalentTo(new[] { valid });
    }
}
