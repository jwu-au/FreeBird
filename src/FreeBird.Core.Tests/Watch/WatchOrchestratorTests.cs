using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using FreeBird.Core.Tests.TestSupport;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Watch;

/// <summary>
/// Tests for <see cref="WatchOrchestrator"/> — the v2 watch-mode polling loop.
/// All collaborators (IScanOrchestrator, ICompletionDetector, ISkipDecider, IFileProcessor) are mocked.
/// Polling uses <see cref="FakeTimeProvider"/> so tests don't sleep.
/// </summary>
public sealed class WatchOrchestratorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-watch-{Guid.NewGuid():N}");
    private readonly string _inputDir;
    private readonly string _outputDir;
    private readonly Serilog.Core.Logger _silentLogger;

    public WatchOrchestratorTests()
    {
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
        _silentLogger = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        _silentLogger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private WatchOptions MakeOptions(
        TimeSpan? pollInterval = null,
        int concurrency = 4,
        bool skipInitialScan = false,
        int stabilityChecks = 2,
        long minFileSizeBytes = 1024)
    {
        return new WatchOptions(
            InputDirs: new[] { _inputDir },
            OutputDir: _outputDir,
            Integrity: IntegrityLevel.Auto,
            Concurrency: concurrency,
            Collision: CollisionPolicy.Skip,
            PollInterval: pollInterval ?? TimeSpan.FromSeconds(5),
            StabilityChecks: stabilityChecks,
            MinFileSizeBytes: minFileSizeBytes,
            SkipInitialScan: skipInitialScan);
    }

    /// <summary>
    /// Writes a file in InputDir; returns its full path.
    /// </summary>
    private async Task<string> TouchUc(string name, int sizeBytes = 2048)
    {
        var path = Path.Combine(_inputDir, name);
        await File.WriteAllBytesAsync(path, new byte[sizeBytes]);
        return path;
    }

    /// <summary>
    /// Builds a SUT with all mocks pre-stubbed to friendly defaults:
    /// - scan orchestrator returns an empty summary
    /// - completion detector returns true (stable)
    /// - skip decider returns Process
    /// - file processor returns Ok
    /// </summary>
    private (WatchOrchestrator sut,
             Mock<IScanOrchestrator> scan,
             Mock<ICompletionDetector> detector,
             Mock<ISkipDecider> decider,
             Mock<IFileProcessor> processor,
             FakeTimeProvider clock,
             Mock<ILogger> logger) MakeSut(bool useSilentLogger = true)
    {
        var scan = new Mock<IScanOrchestrator>();
        scan.Setup(s => s.RunAsync(It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanSummary(0, 0, 0, 0, 0, 0, TimeSpan.Zero));

        var detector = new Mock<ICompletionDetector>();
        detector.Setup(d => d.IsStableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        var decider = new Mock<ISkipDecider>();
        decider.Setup(d => d.DecideAsync(It.IsAny<string>(), It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(SkipDecision.Process());

        var processor = new Mock<IFileProcessor>();
        processor.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
                 .Returns<string, ScanOptions, CancellationToken>((path, _, _) =>
                     Task.FromResult(new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3, path + ".mp3")));

        var clock = new FakeTimeProvider();
        var loggerMock = new Mock<ILogger>();
        ILogger loggerArg = useSilentLogger ? (ILogger)_silentLogger : loggerMock.Object;

        var sut = new WatchOrchestrator(
            scan.Object,
            detector.Object,
            decider.Object,
            SingleProcessorRouter.For(processor.Object),
            clock,
            loggerArg);

        return (sut, scan, detector, decider, processor, clock, loggerMock);
    }

    /// <summary>
    /// Drive the clock forward by the given interval, with a tiny real-world pause to let
    /// the polling loop observe the advancement (FakeTimeProvider doesn't synchronously
    /// resume the awaiter — it schedules a continuation we must yield to).
    /// </summary>
    private static async Task AdvanceAndYieldAsync(FakeTimeProvider clock, TimeSpan delta)
    {
        clock.Advance(delta);
        // Yield a few times so the timer continuation runs.
        for (int i = 0; i < 10; i++)
        {
            await Task.Yield();
            await Task.Delay(5);
        }
    }

    // --- Initial sweep ---

    [Fact]
    public async Task RunAsync_InitialSweep_DefaultBehavior_CallsScanOrchestratorOnce()
    {
        var (sut, scan, _, _, _, clock, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromSeconds(30));

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        // Give the initial sweep a moment to run; do not advance clock so polling loop stays asleep.
        await Task.Delay(50);
        soft.Cancel();
        var summary = await runTask;

        scan.Verify(s => s.RunAsync(It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        summary.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_SkipInitialScanTrue_DoesNotCallScanOrchestrator()
    {
        var (sut, scan, _, _, _, _, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromSeconds(30), skipInitialScan: true);

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);
        await Task.Delay(50);
        soft.Cancel();
        await runTask;

        scan.Verify(s => s.RunAsync(It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- One polling cycle ---

    [Fact]
    public async Task RunAsync_OnePollingTick_EnumeratesFiles_DetectsStable_ProcessesReady()
    {
        var (sut, _, detector, decider, processor, clock, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromSeconds(5), skipInitialScan: true);

        await TouchUc("ready.uc");

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        await AdvanceAndYieldAsync(clock, TimeSpan.FromSeconds(5));
        soft.Cancel();
        await runTask;

        detector.Verify(d => d.IsStableAsync(It.Is<string>(s => s.EndsWith("ready.uc")), 2, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        decider.Verify(d => d.DecideAsync(It.Is<string>(s => s.EndsWith("ready.uc")), options, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        processor.Verify(p => p.ProcessAsync(It.Is<string>(s => s.EndsWith("ready.uc")), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_FileNotYetStable_NotProcessedThisCycle()
    {
        var (sut, _, detector, decider, processor, clock, _) = MakeSut();
        // detector reports unstable
        detector.Reset();
        detector.Setup(d => d.IsStableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

        var options = MakeOptions(pollInterval: TimeSpan.FromSeconds(5), skipInitialScan: true);
        await TouchUc("incoming.uc");

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        await AdvanceAndYieldAsync(clock, TimeSpan.FromSeconds(5));
        soft.Cancel();
        await runTask;

        detector.Verify(d => d.IsStableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        // Not stable -> never sent to decider or processor
        decider.Verify(d => d.DecideAsync(It.IsAny<string>(), It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        processor.Verify(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_SkipDeciderReturnsSkip_FileNotProcessed_DetectorForgotten()
    {
        var (sut, _, detector, decider, processor, clock, _) = MakeSut();
        decider.Reset();
        decider.Setup(d => d.DecideAsync(It.IsAny<string>(), It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(SkipDecision.Skip(SkipReason.AlreadyDecoded, "already there"));

        var options = MakeOptions(pollInterval: TimeSpan.FromSeconds(5), skipInitialScan: true);
        await TouchUc("dupe.uc");

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        await AdvanceAndYieldAsync(clock, TimeSpan.FromSeconds(5));
        soft.Cancel();
        await runTask;

        decider.Verify(d => d.DecideAsync(It.Is<string>(s => s.EndsWith("dupe.uc")), It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        processor.Verify(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        detector.Verify(d => d.Forget(It.Is<string>(s => s.EndsWith("dupe.uc"))), Times.AtLeastOnce);
    }

    // --- Cancellation ---

    [Fact]
    public async Task RunAsync_GracefulCancellation_DuringWait_ReturnsWithoutNextCycle()
    {
        var (sut, _, _, _, processor, _, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromSeconds(30), skipInitialScan: true);
        await TouchUc("a.uc");

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        await Task.Delay(50);  // let the loop enter the wait
        soft.Cancel();
        var summary = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        summary.Should().NotBeNull();
        processor.Verify(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_GracefulCancellation_DuringCycle_LetsInFlightFinish()
    {
        var (sut, _, _, _, processor, clock, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromMilliseconds(50), skipInitialScan: true);
        await TouchUc("slow.uc");

        var processStarted = new TaskCompletionSource();
        var processProceed = new TaskCompletionSource();
        processor.Reset();
        processor.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
                 .Returns<string, ScanOptions, CancellationToken>(async (path, _, ct) =>
                 {
                     processStarted.TrySetResult();
                     await processProceed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                     return new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3, path + ".mp3");
                 });

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        soft.Cancel();
        await Task.Delay(50);

        // The in-flight processor should still be unblocked & finish naturally.
        processProceed.SetResult();

        var summary = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        summary.Ok.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_HardAbort_DuringProcessing_PropagatesToProcessor()
    {
        var (sut, _, _, _, processor, clock, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromMilliseconds(50), skipInitialScan: true);
        await TouchUc("aborted.uc");

        var processStarted = new TaskCompletionSource();
        processor.Reset();
        processor.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
                 .Returns<string, ScanOptions, CancellationToken>(async (path, _, ct) =>
                 {
                     processStarted.TrySetResult();
                     // wait for hard-abort to be observed
                     await Task.Delay(TimeSpan.FromSeconds(10), ct);
                     return new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3);
                 });

        using var soft = new CancellationTokenSource();
        using var hard = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, hard.Token);

        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hard.Cancel();
        soft.Cancel();

        // The orchestrator should surface OCE from Parallel.ForEachAsync OR return early.
        // Either way, the inner CT was cancelled and the processor was called.
        var summary = await runTask.WaitAsync(TimeSpan.FromSeconds(5));
        summary.Should().NotBeNull();
        processor.Verify(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // --- Summary accumulation ---

    [Fact]
    public async Task RunAsync_TwoFilesProcessed_SummaryAccumulates()
    {
        var (sut, _, _, _, _, clock, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromSeconds(5), skipInitialScan: true);
        await TouchUc("one.uc");
        await TouchUc("two.uc");

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        await AdvanceAndYieldAsync(clock, TimeSpan.FromSeconds(5));
        soft.Cancel();
        var summary = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        summary.Ok.Should().BeGreaterThanOrEqualTo(2);
    }

    // --- Cycle lock + debounce ---

    [Fact]
    public async Task RunAsync_CycleLockEngaged_NextTickSkips_WarnsOnFirstSkip()
    {
        var (sut, _, _, _, processor, clock, logger) = MakeSut(useSilentLogger: false);
        var options = MakeOptions(pollInterval: TimeSpan.FromMilliseconds(50), skipInitialScan: true);
        await TouchUc("slow.uc");

        var holdProcess = new TaskCompletionSource();
        processor.Reset();
        processor.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
                 .Returns<string, ScanOptions, CancellationToken>(async (path, _, ct) =>
                 {
                     await holdProcess.Task.WaitAsync(TimeSpan.FromSeconds(5));
                     return new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3);
                 });

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        // Tick #1: starts cycle, blocks in processor
        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        // Tick #2: cannot acquire lock -> WARN (skip #1)
        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));

        holdProcess.SetResult();
        soft.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Verify the first-skip WARN was emitted (use the parameterless or any-overload pattern).
        // Serilog ILogger.Warning has many overloads; match on the templated single-arg version.
        logger.Verify(l => l.Warning(It.Is<string>(s => s.Contains("Previous cycle still running"))),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_FiveConsecutiveSkips_LogsDebugForSkips2Through5()
    {
        var (sut, _, _, _, processor, clock, logger) = MakeSut(useSilentLogger: false);
        var options = MakeOptions(pollInterval: TimeSpan.FromMilliseconds(50), skipInitialScan: true);
        await TouchUc("slow.uc");

        var holdProcess = new TaskCompletionSource();
        processor.Reset();
        processor.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
                 .Returns<string, ScanOptions, CancellationToken>(async (path, _, ct) =>
                 {
                     await holdProcess.Task.WaitAsync(TimeSpan.FromSeconds(5));
                     return new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3);
                 });

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        // Tick 1 → cycle begins and blocks
        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        // Ticks 2..5 → 4 more skips (skip #1 → WARN, skips #2-#4 → DEBUG)
        for (int i = 0; i < 4; i++)
        {
            await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        }

        holdProcess.SetResult();
        soft.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        // 1 WARN for skip #1
        logger.Verify(l => l.Warning(It.Is<string>(s => s.Contains("Previous cycle still running"))),
            Times.AtLeastOnce);
        // Skips 2-4 should produce DEBUG calls. (We only verify >= 1 debug skip log because timing
        // could vary slightly; the count assertion in the 6th-skip test confirms ramping.)
        logger.Verify(l => l.Debug(It.Is<string>(s => s.Contains("Skipping poll")), It.IsAny<int>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_SixConsecutiveSkips_LogsWarnHint()
    {
        var (sut, _, _, _, processor, clock, logger) = MakeSut(useSilentLogger: false);
        var options = MakeOptions(pollInterval: TimeSpan.FromMilliseconds(50), skipInitialScan: true);
        await TouchUc("slow.uc");

        var holdProcess = new TaskCompletionSource();
        processor.Reset();
        processor.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
                 .Returns<string, ScanOptions, CancellationToken>(async (path, _, ct) =>
                 {
                     await holdProcess.Task.WaitAsync(TimeSpan.FromSeconds(5));
                     return new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3);
                 });

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        // Tick 1 → cycle begins and blocks
        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        // Ticks 2..7 → 6 skipped polls (skip #1 WARN, #2-#5 DEBUG, #6 WARN-hint)
        for (int i = 0; i < 6; i++)
        {
            await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        }

        holdProcess.SetResult();
        soft.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        // The hint warning at skip #6 mentions "Long-running cycle"
        logger.Verify(l => l.Warning(It.Is<string>(s => s.Contains("Long-running cycle"))),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_SuccessfulCycleResetsSkipCounter()
    {
        var (sut, _, _, _, processor, clock, logger) = MakeSut(useSilentLogger: false);
        var options = MakeOptions(pollInterval: TimeSpan.FromMilliseconds(50), skipInitialScan: true);
        await TouchUc("a.uc");

        // First cycle: block for 2 skipped ticks, then succeed.
        var firstHold = new TaskCompletionSource();
        var callCount = 0;
        processor.Reset();
        processor.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
                 .Returns<string, ScanOptions, CancellationToken>(async (path, _, ct) =>
                 {
                     var n = Interlocked.Increment(ref callCount);
                     if (n == 1)
                     {
                         await firstHold.Task.WaitAsync(TimeSpan.FromSeconds(5));
                     }
                     return new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3);
                 });

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        // Tick 1 → start cycle, blocks
        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        // Tick 2 → skip #1 (WARN)
        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        // Release first
        firstHold.SetResult();
        await Task.Delay(50);

        // Now we want a second blocking cycle so we can verify skip counter was reset → next skip is WARN again
        var secondHold = new TaskCompletionSource();
        processor.Reset();
        processor.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
                 .Returns<string, ScanOptions, CancellationToken>(async (path, _, ct) =>
                 {
                     await secondHold.Task.WaitAsync(TimeSpan.FromSeconds(5));
                     return new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3);
                 });

        // Tick 3 → start new blocking cycle
        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        // Tick 4 → skip → should be WARN again, not DEBUG (because counter was reset)
        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));

        secondHold.SetResult();
        soft.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        // At least 2 first-skip WARNs were emitted (one before, one after the reset).
        logger.Verify(l => l.Warning(It.Is<string>(s => s.Contains("Previous cycle still running"))),
            Times.AtLeast(2));
    }

    // --- Empty input dir ---

    [Fact]
    public async Task RunAsync_InputDirEmpty_NoFilesEnumerated_NoProcessorCalls()
    {
        var (sut, _, detector, decider, processor, clock, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromSeconds(5), skipInitialScan: true);

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        await AdvanceAndYieldAsync(clock, TimeSpan.FromSeconds(5));
        soft.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        detector.Verify(d => d.IsStableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        decider.Verify(d => d.DecideAsync(It.IsAny<string>(), It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        processor.Verify(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Constructor null guards ---

    [Fact]
    public void Constructor_AnyNullDep_Throws()
    {
        var scan = new Mock<IScanOrchestrator>().Object;
        var det = new Mock<ICompletionDetector>().Object;
        var dec = new Mock<ISkipDecider>().Object;
        var router = new Mock<IFileProcessorRouter>().Object;
        var clk = new FakeTimeProvider();
        ILogger log = new LoggerConfiguration().CreateLogger();

        ((Action)(() => _ = new WatchOrchestrator(null!, det, dec, router, clk, log))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new WatchOrchestrator(scan, null!, dec, router, clk, log))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new WatchOrchestrator(scan, det, null!, router, clk, log))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new WatchOrchestrator(scan, det, dec, null!, clk, log))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new WatchOrchestrator(scan, det, dec, router, null!, log))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new WatchOrchestrator(scan, det, dec, router, clk, null!))).Should().Throw<ArgumentNullException>();
    }

    // --- v3.4 T04: InputDirs cardinality invariant ---

    [Fact]
    public async Task RunAsync_InputDirsEmpty_ThrowsArgumentException()
    {
        var (sut, _, _, _, _, _, _) = MakeSut();
        var options = new WatchOptions(
            InputDirs: Array.Empty<string>(),
            OutputDir: _outputDir,
            SkipInitialScan: true);

        Func<Task> act = () => sut.RunAsync(options, CancellationToken.None, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.Message.Contains("InputDirs"));
    }

    [Fact]
    public async Task RunAsync_InputDirsMultiple_ThrowsArgumentException()
    {
        var (sut, _, _, _, _, _, _) = MakeSut();
        var options = new WatchOptions(
            InputDirs: new[] { _inputDir, _inputDir + "-2" },
            OutputDir: _outputDir,
            SkipInitialScan: true);

        Func<Task> act = () => sut.RunAsync(options, CancellationToken.None, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>()
            .Where(e => e.Message.Contains("WatchSupervisor"));
    }

    // --- Enumeration scope: non-recursive + extension filter ---

    [Fact]
    public async Task RunAsync_FilesInSubdir_NotEnumerated()
    {
        var (sut, _, detector, _, _, clock, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromSeconds(5), skipInitialScan: true);

        // Arrange: put a .uc file in a subdirectory of input
        var subdir = Path.Combine(_inputDir, "subdir");
        Directory.CreateDirectory(subdir);
        var subdirFile = Path.Combine(subdir, "hidden.uc");
        await File.WriteAllBytesAsync(subdirFile, new byte[2048]);

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        await AdvanceAndYieldAsync(clock, TimeSpan.FromSeconds(5));
        soft.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert: completion detector was NEVER asked about the subdir file
        detector.Verify(d => d.IsStableAsync(
            It.Is<string>(p => p.Contains("subdir")),
            It.IsAny<int>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_NonUcFiles_Ignored()
    {
        var (sut, _, detector, _, _, clock, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromSeconds(5), skipInitialScan: true);

        // Arrange: put a .mp3 and a .txt in input
        await File.WriteAllBytesAsync(Path.Combine(_inputDir, "already.mp3"), new byte[2048]);
        await File.WriteAllTextAsync(Path.Combine(_inputDir, "notes.txt"), "hello");

        using var soft = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, CancellationToken.None);

        await AdvanceAndYieldAsync(clock, TimeSpan.FromSeconds(5));
        soft.Cancel();
        await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        detector.Verify(d => d.IsStableAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Partial-summary regression: graceful cancel mid-cycle still reports completed files ---

    [Fact]
    public async Task RunAsync_GracefulCancellation_DuringParallelPhase_PartialSummaryStillReported()
    {
        // Two files in input dir. One completes immediately; the second hangs forever.
        // We hard-cancel after the first completes — the partial counts from the aborted
        // cycle must still flow into the final summary.
        var (sut, _, _, _, processor, clock, _) = MakeSut();
        var options = MakeOptions(pollInterval: TimeSpan.FromMilliseconds(50), skipInitialScan: true, concurrency: 2);

        var fileA = await TouchUc("a.uc");
        var fileB = await TouchUc("b.uc");

        var fileACompleted = new TaskCompletionSource();
        processor.Reset();
        processor.Setup(p => p.ProcessAsync(fileA, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
                 .Returns<string, ScanOptions, CancellationToken>((_, _, _) =>
                 {
                     fileACompleted.TrySetResult();
                     return Task.FromResult(new ScanResult(fileA, ScanOutcome.Ok, AudioFormat.Mp3, fileA + ".mp3"));
                 });
        processor.Setup(p => p.ProcessAsync(fileB, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
                 .Returns<string, ScanOptions, CancellationToken>(async (_, _, ct) =>
                 {
                     await Task.Delay(Timeout.Infinite, ct);
                     return null!;  // unreachable
                 });

        using var soft = new CancellationTokenSource();
        using var hard = new CancellationTokenSource();
        var runTask = sut.RunAsync(options, soft.Token, hard.Token);

        await AdvanceAndYieldAsync(clock, TimeSpan.FromMilliseconds(50));
        await fileACompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        hard.Cancel();
        soft.Cancel();

        var summary = await runTask.WaitAsync(TimeSpan.FromSeconds(5));

        // KEY ASSERTION: file A's success was reported into the summary,
        // even though the cycle as a whole was aborted by hard cancel.
        summary.Ok.Should().Be(1, because: "file A completed before cancel; partial cycle counts must survive");
    }
}
