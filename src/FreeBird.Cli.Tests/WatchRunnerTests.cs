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
/// T11 — WatchRunner integration tests. Substitutes a mock IWatchOrchestrator via
/// <see cref="WatchRunner.OrchestratorFactoryOverride"/> so the watch loop never actually runs.
/// Shares <c>ConsoleRedirect</c> collection with <see cref="WatchCommandTests"/> to prevent
/// the two suites from racing on <c>Console.Out</c>.
/// </summary>
[Collection("GlobalStaticState")]
public class WatchRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public WatchRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-watchrunner-{Guid.NewGuid():N}");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        WatchRunner.OrchestratorFactoryOverride = null;
        WatchRunner.CoordinatorFactoryOverride = null;
        WatchRunner.AdditionalContainerSetup = null;
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private WatchOptions MakeOpts(string? logFile = null, bool noLogFile = false, IntegrityLevel integrity = IntegrityLevel.Off) => new()
    {
        InputDirs = new[] { _inputDir },
        OutputDir = _outputDir,
        Integrity = integrity,
        Concurrency = 2,
        Collision = CollisionPolicy.Skip,
        PollInterval = TimeSpan.FromSeconds(5),
        StabilityChecks = 2,
        MinFileSize = 1024,
        SkipInitialScan = true,
        LogFilePath = logFile,
        NoLogFile = noLogFile,
        Verbose = false,
        Quiet = false,
    };

    private static ScanSummary EmptySummary() =>
        new(Processed: 0, Ok: 0, Skipped: 0, UnknownFormat: 0, IntegrityFailed: 0, Errors: 0, Duration: TimeSpan.Zero);

    [Fact]
    public async Task Run_DelegatesToOrchestrator_AndReturnsZero_WhenSummaryClean()
    {
        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptySummary());
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(noLogFile: true), CancellationToken.None);

        exit.Should().Be(0);
        mock.Verify(o => o.RunAsync(
            It.IsAny<FreeBird.Core.Models.WatchOptions>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Run_ReturnsOne_WhenFailedNonZero()
    {
        var failing = new ScanSummary(
            Processed: 5, Ok: 3, Skipped: 0, UnknownFormat: 0,
            IntegrityFailed: 2, Errors: 0, Duration: TimeSpan.FromSeconds(1));
        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(failing);
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(noLogFile: true), CancellationToken.None);

        exit.Should().Be(1);
    }

    [Fact]
    public async Task Run_CallsOrchestrator_WithMappedCoreOptions()
    {
        FreeBird.Core.Models.WatchOptions? captured = null;
        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .Callback<FreeBird.Core.Models.WatchOptions, CancellationToken, CancellationToken>((opts, _, _) => captured = opts)
            .ReturnsAsync(EmptySummary());
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        var cli = new WatchOptions
        {
            InputDirs = new[] { _inputDir },
            OutputDir = _outputDir,
            Integrity = IntegrityLevel.L1,
            Concurrency = 8,
            Collision = CollisionPolicy.Overwrite,
            PollInterval = TimeSpan.FromSeconds(13),
            StabilityChecks = 4,
            MinFileSize = 2048,
            SkipInitialScan = true,
            NoLogFile = true,
        };

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(cli, CancellationToken.None);

        exit.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.InputDirs.Should().BeEquivalentTo(new[] { _inputDir });
        captured.OutputDir.Should().Be(_outputDir);
        captured.Integrity.Should().Be(IntegrityLevel.L1);
        captured.Concurrency.Should().Be(8);
        captured.Collision.Should().Be(CollisionPolicy.Overwrite);
        captured.PollInterval.Should().Be(TimeSpan.FromSeconds(13));
        captured.StabilityChecks.Should().Be(4);
        captured.MinFileSizeBytes.Should().Be(2048);
        captured.SkipInitialScan.Should().BeTrue();
    }

    [Fact]
    public async Task Run_CreatesLogDirectory_IfMissing()
    {
        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptySummary());
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(), CancellationToken.None);

        exit.Should().Be(0);
        var expectedLogDir = Path.Combine(_outputDir, ".freebird", "logs");
        Directory.Exists(expectedLogDir).Should().BeTrue();
        // Should have created at least one log file (Serilog appends -YYYY-MM-DD.log)
        Directory.GetFiles(expectedLogDir, "watch-*.log").Should().NotBeEmpty();
    }

    [Fact]
    public async Task Run_NoLogFileFlag_SkipsFileSinkSetup()
    {
        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptySummary());
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(noLogFile: true), CancellationToken.None);

        exit.Should().Be(0);
        var defaultLogDir = Path.Combine(_outputDir, ".freebird", "logs");
        // Directory should NOT have been created (no file sink set up)
        Directory.Exists(defaultLogDir).Should().BeFalse();
    }

    [Fact]
    public async Task Run_LogFileExplicitPath_Honored()
    {
        var customLogPath = Path.Combine(_tempDir, "custom", "my-watch.log");
        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptySummary());
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(logFile: customLogPath), CancellationToken.None);

        exit.Should().Be(0);
        // Serilog with explicit path + RollingInterval.Day inserts the date before the extension:
        // "my-watch.log" -> "my-watch20260610.log" (or similar). Just check directory + any file.
        var customDir = Path.GetDirectoryName(customLogPath)!;
        Directory.Exists(customDir).Should().BeTrue();
        Directory.GetFiles(customDir, "my-watch*.log").Should().NotBeEmpty();
    }

    [Fact]
    public async Task Run_PrintsSummaryToConsole()
    {
        var summary = new ScanSummary(
            Processed: 10, Ok: 7, Skipped: 2, UnknownFormat: 0,
            IntegrityFailed: 1, Errors: 0, Duration: TimeSpan.FromSeconds(3));
        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        var origOut = Console.Out;
        var buf = new StringWriter();
        Console.SetOut(buf);
        int exit;
        try
        {
            var runner = new WatchRunner();
            exit = await runner.RunAsync(MakeOpts(noLogFile: true), CancellationToken.None);
        }
        finally
        {
            Console.SetOut(origOut);
        }

        exit.Should().Be(1);
        var output = buf.ToString();
        // Summary line should mention key counters.
        output.Should().Contain("Processed=10");
        output.Should().Contain("Ok=7");
        output.Should().Contain("Skipped=2");
        output.Should().Contain("IntegrityFailed=1");
    }

    // --- T12: SIGINT/SIGTERM handler + exit code 130 mapping ---

    [Fact]
    public async Task Run_SignalCountNonZero_Returns130_EvenIfSummaryClean()
    {
        // Orchestrator returns a clean summary, but the coordinator reports a signal was
        // received. WatchRunner must honor the Unix convention and return 130 rather than 0.
        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptySummary());
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        WatchRunner.CoordinatorFactoryOverride = logger =>
        {
            var c = new CancellationCoordinator(TimeProvider.System, logger);
            c.OnCancelRequested();   // simulate one Ctrl-C — SignalCount becomes 1
            return c;
        };

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(noLogFile: true), CancellationToken.None);

        exit.Should().Be(130);
    }

    [Fact]
    public async Task Run_SignalCountNonZero_Returns130_EvenWithFailures()
    {
        // Even when the summary has failures, the signal-driven exit code (130) takes precedence
        // because the user's abort is the more informative signal to the calling shell.
        var failing = new ScanSummary(
            Processed: 5, Ok: 3, Skipped: 0, UnknownFormat: 0,
            IntegrityFailed: 2, Errors: 0, Duration: TimeSpan.FromSeconds(1));
        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(failing);
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        WatchRunner.CoordinatorFactoryOverride = logger =>
        {
            var c = new CancellationCoordinator(TimeProvider.System, logger);
            c.OnHardSignalRequested();   // simulate SIGTERM — SignalCount becomes 1
            return c;
        };

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(noLogFile: true), CancellationToken.None);

        exit.Should().Be(130);
    }

    [Fact]
    public async Task Run_OrchestratorReceives_TwoDistinctTokens_FromCoordinator()
    {
        // Verify the runner threads TWO distinct tokens (graceful + hard) through to the
        // orchestrator's RunAsync — not the same single token as in T11.
        CancellationToken capturedGraceful = default;
        CancellationToken capturedHard = default;
        bool capturedDuringCall = false;
        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .Callback<FreeBird.Core.Models.WatchOptions, CancellationToken, CancellationToken>(
                (_, g, h) =>
                {
                    capturedGraceful = g;
                    capturedHard = h;
                    capturedDuringCall = true;
                })
            .ReturnsAsync(EmptySummary());
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        var runner = new WatchRunner();
        var exit = await runner.RunAsync(MakeOpts(noLogFile: true), CancellationToken.None);

        exit.Should().Be(0);
        capturedDuringCall.Should().BeTrue();
        // The two tokens passed to the orchestrator must be distinct (different underlying CTS).
        capturedGraceful.Should().NotBe(capturedHard);
    }

    // --- T19-fixup: external-token cancel must NOT bump CancellationCoordinator.SignalCount ---

    [Fact]
    public async Task Run_ExternalTokenCancelled_DoesNotIncrementCoordinatorSignalCount()
    {
        // Regression guard for the T19 production bug: System.CommandLine 3.x installs its own
        // SIGINT handler that cancels the external token passed into RunAsync. If WatchRunner
        // bridges that external token into CancellationCoordinator.OnCancelRequested(), a single
        // real SIGINT increments SignalCount TWICE (once from the external bridge, once from
        // Console.CancelKeyPress) and immediately escalates to hard abort, bypassing the 5s
        // graceful drain entirely.
        //
        // Fix: external token cancellation must gracefully stop the runner without touching the
        // coordinator's signal-count state machine. Only real OS signals (SIGINT/SIGTERM via the
        // Subscribe* wiring) should bump SignalCount.
        CancellationCoordinator? capturedCoord = null;
        WatchRunner.CoordinatorFactoryOverride = log =>
        {
            var c = new CancellationCoordinator(TimeProvider.System, log);
            capturedCoord = c;
            return c;
        };

        var mock = new Mock<IWatchOrchestrator>();
        mock.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .Returns<FreeBird.Core.Models.WatchOptions, CancellationToken, CancellationToken>(
                async (_, gract, _) =>
                {
                    // Block until the graceful token fires, then return a clean summary —
                    // emulating an orchestrator that respects graceful cancellation.
                    try { await Task.Delay(Timeout.Infinite, gract); }
                    catch (OperationCanceledException) { /* expected */ }
                    return EmptySummary();
                });
        WatchRunner.OrchestratorFactoryOverride = _ => mock.Object;

        using var cts = new CancellationTokenSource();
        var runner = new WatchRunner();
        var runTask = runner.RunAsync(MakeOpts(noLogFile: true), cts.Token);

        // Give the runner a beat to register handlers and call into the orchestrator.
        await Task.Delay(100);
        cts.Cancel();
        var exit = await runTask;

        capturedCoord.Should().NotBeNull("the coordinator factory override should have run");
        capturedCoord!.SignalCount.Should().Be(0,
            because: "external-token cancellation must NOT bump SignalCount — only real OS " +
                     "signals (SIGINT/SIGTERM) should. Otherwise System.CommandLine's own " +
                     "SIGINT handler double-counts and immediately escalates to hard abort.");
        // Because no real signal was delivered, exit code must NOT be 130. A clean orchestrator
        // return with SignalCount==0 is exit 0.
        exit.Should().Be(0,
            because: "external cancel with no signals produces a clean summary and exit 0");
    }

    // --- T13: startup flac probe + integrity-mode degradation in WatchRunner ---

    [Fact]
    public async Task Run_L3_FlacNotAvailable_ExitsBadArgs()
    {
        // Parity with ScanRunner: --integrity l3 + no flac binary on PATH must fail
        // fast with ExitBadArgs(2) at startup, BEFORE the orchestrator is invoked.
        var probe = new Mock<IFlacProbe>();
        probe.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptySummary());

        WatchRunner.AdditionalContainerSetup = b =>
        {
            b.RegisterInstance(probe.Object).As<IFlacProbe>().SingleInstance();
            b.RegisterInstance(orch.Object).As<IWatchOrchestrator>().SingleInstance();
        };

        var opts = MakeOpts(noLogFile: true, integrity: IntegrityLevel.L3);
        var runner = new WatchRunner();
        var exit = await runner.RunAsync(opts, CancellationToken.None);

        exit.Should().Be(WatchRunner.ExitBadArgs);
        // Orchestrator MUST NOT have been called — we failed before reaching the loop.
        orch.Verify(o => o.RunAsync(
            It.IsAny<FreeBird.Core.Models.WatchOptions>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_Auto_FlacNotAvailable_WarnsButProceeds()
    {
        // Parity with ScanRunner: --integrity auto + no flac binary -> WARN + continue.
        // Exit code matches the orchestrator outcome (clean summary => ExitOk).
        var probe = new Mock<IFlacProbe>();
        probe.Setup(p => p.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(
                It.IsAny<FreeBird.Core.Models.WatchOptions>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptySummary());

        WatchRunner.AdditionalContainerSetup = b =>
        {
            b.RegisterInstance(probe.Object).As<IFlacProbe>().SingleInstance();
            b.RegisterInstance(orch.Object).As<IWatchOrchestrator>().SingleInstance();
        };

        var opts = MakeOpts(noLogFile: true, integrity: IntegrityLevel.Auto);
        var runner = new WatchRunner();
        var exit = await runner.RunAsync(opts, CancellationToken.None);

        exit.Should().Be(WatchRunner.ExitOk);
        // Orchestrator SHOULD have been called — auto degrades silently to L1.
        orch.Verify(o => o.RunAsync(
            It.IsAny<FreeBird.Core.Models.WatchOptions>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
