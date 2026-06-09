using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
[Collection("ConsoleRedirect")]
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
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private WatchOptions MakeOpts(string? logFile = null, bool noLogFile = false) => new()
    {
        InputDir = _inputDir,
        OutputDir = _outputDir,
        Integrity = IntegrityLevel.Off,
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
            InputDir = _inputDir,
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
        captured!.InputDir.Should().Be(_inputDir);
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
}
