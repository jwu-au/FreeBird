using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Watch;

/// <summary>
/// Tests for <see cref="WatchTask"/> runtime delegation methods <c>RunAsync</c>
/// and <c>Cancel</c> (T07). Covers normal completion, cancellation, crash recording,
/// fatal-threshold graceful exit, and the crash-injection test seam.
/// </summary>
public sealed class WatchTaskRuntimeTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly Serilog.Core.Logger _silentLogger;

    public WatchTaskRuntimeTests()
    {
        _silentLogger = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        _silentLogger.Dispose();
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } } catch { }
        }
    }

    private string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fb-watchtask-rt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private string NewNonExistentPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fb-watchtask-rt-missing-{Guid.NewGuid():N}");
        _tempDirs.Add(path);
        return path;
    }

    private WatchInput MakeInput(string path) => WatchInput.FromPath(path);

    private static WatchOptions MakeOptions(string inputDir, string outputDir)
        => new WatchOptions(new[] { inputDir }, outputDir);

    private static ScanSummary MakeSummary(int ok = 0, int processed = 0)
        => new ScanSummary(
            Processed: processed,
            Ok: ok,
            Skipped: 0,
            UnknownFormat: 0,
            IntegrityFailed: 0,
            Errors: 0,
            Duration: TimeSpan.Zero);

    // ---- Test 1 ----
    [Fact]
    public async Task RunAsync_NullOptions_Throws()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var orch = new Mock<IWatchOrchestrator>().Object;
        var task = new WatchTask(input, () => orch, new FakeTimeProvider(), _silentLogger);

        Func<Task> act = () => task.RunAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---- Test 2 ----
    [Fact]
    public async Task RunAsync_BornDeadTask_ReturnsEmptySummary_WithoutCallingOrchestrator()
    {
        var missing = NewNonExistentPath();
        var input = MakeInput(missing);
        var factoryCalls = 0;
        var mockOrch = new Mock<IWatchOrchestrator>(MockBehavior.Strict);
        Func<IWatchOrchestrator> factory = () => { factoryCalls++; return mockOrch.Object; };
        var task = new WatchTask(input, factory, new FakeTimeProvider(), _silentLogger);
        task.State.Should().Be(WatchTaskState.Dead);

        var summary = await task.RunAsync(MakeOptions(missing, NewTempDir()), CancellationToken.None);

        summary.Should().NotBeNull();
        summary.Processed.Should().Be(0);
        summary.Ok.Should().Be(0);
        factoryCalls.Should().Be(0);
        mockOrch.Verify(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Test 3 ----
    [Fact]
    public async Task RunAsync_HappyPath_DelegatesToWrappedOrchestrator_ReturnsItsResult()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var expected = MakeSummary(ok: 5, processed: 5);
        var mockOrch = new Mock<IWatchOrchestrator>();
        mockOrch
            .Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var task = new WatchTask(input, () => mockOrch.Object, new FakeTimeProvider(), _silentLogger);

        var result = await task.RunAsync(MakeOptions(dir, NewTempDir()), CancellationToken.None);

        result.Should().Be(expected);
        task.State.Should().Be(WatchTaskState.Active);
        mockOrch.Verify(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Test 4 ----
    [Fact]
    public async Task RunAsync_ExternalCancellation_PropagatesOCE()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var mockOrch = new Mock<IWatchOrchestrator>();
        mockOrch
            .Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return MakeSummary();
            });
        var task = new WatchTask(input, () => mockOrch.Object, new FakeTimeProvider(), _silentLogger);
        using var cts = new CancellationTokenSource();

        var run = task.RunAsync(MakeOptions(dir, NewTempDir()), cts.Token);
        cts.Cancel();

        Func<Task> act = () => run;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Test 5 ----
    [Fact]
    public async Task RunAsync_Cancel_MethodSignalsLinkedCts()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var entered = new TaskCompletionSource();
        var mockOrch = new Mock<IWatchOrchestrator>();
        mockOrch
            .Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return MakeSummary();
            });
        var task = new WatchTask(input, () => mockOrch.Object, new FakeTimeProvider(), _silentLogger);

        var run = task.RunAsync(MakeOptions(dir, NewTempDir()), CancellationToken.None);
        // Wait for the orchestrator to actually enter its Task.Delay (so _taskCts is set
        // and the awaiter is registered) before invoking Cancel — eliminates the race
        // where Cancel() fires before the linked CTS exists.
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        task.Cancel();

        Func<Task> act = () => run;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Test 6 ----
    [Fact]
    public async Task RunAsync_OrchestratorThrows_NonFatal_RecordsCrashAndRethrows()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var mockOrch = new Mock<IWatchOrchestrator>();
        mockOrch
            .Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var task = new WatchTask(input, () => mockOrch.Object, new FakeTimeProvider(), _silentLogger);

        Func<Task> act = () => task.RunAsync(MakeOptions(dir, NewTempDir()), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        task.State.Should().Be(WatchTaskState.Active); // only 1 crash
    }

    // ---- Test 7 ----
    [Fact]
    public async Task RunAsync_OrchestratorThrows_FatalThreshold_RecordsCrashReturnsEmpty()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var clock = new FakeTimeProvider();

        // Use crash injector to deterministically force three crashes from RunAsync.
        var crashes = 0;
        Func<Exception?> injector = () =>
        {
            crashes++;
            return new InvalidOperationException($"boom #{crashes}");
        };

        var mockOrch = new Mock<IWatchOrchestrator>(MockBehavior.Strict);
        var task = new WatchTask(input, () => mockOrch.Object, clock, _silentLogger, injector);

        // Crash 1
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => task.RunAsync(MakeOptions(dir, NewTempDir()), CancellationToken.None));
        task.State.Should().Be(WatchTaskState.Active);

        // Crash 2
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => task.RunAsync(MakeOptions(dir, NewTempDir()), CancellationToken.None));
        task.State.Should().Be(WatchTaskState.Active);

        // Crash 3 — threshold reached, should return empty summary (graceful DEAD).
        var summary = await task.RunAsync(MakeOptions(dir, NewTempDir()), CancellationToken.None);
        summary.Should().NotBeNull();
        summary.Processed.Should().Be(0);
        summary.Ok.Should().Be(0);
        task.State.Should().Be(WatchTaskState.Dead);
    }

    // ---- Test 8 ----
    [Fact]
    public async Task RunAsync_CrashInjector_ThrowsBeforeOrchestrator()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var mockOrch = new Mock<IWatchOrchestrator>(MockBehavior.Strict);
        // Strict mock: any call to RunAsync would throw "unexpected invocation".
        Func<Exception?> injector = () => new InvalidOperationException("injected");
        var task = new WatchTask(input, () => mockOrch.Object, new FakeTimeProvider(), _silentLogger, injector);

        Func<Task> act = () => task.RunAsync(MakeOptions(dir, NewTempDir()), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("injected");
        mockOrch.Verify(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Test 9 ----
    [Fact]
    public void Cancel_NoActiveRun_NoOp()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var task = new WatchTask(input, () => new Mock<IWatchOrchestrator>().Object, new FakeTimeProvider(), _silentLogger);

        Action act = () => task.Cancel();

        act.Should().NotThrow();
    }

    // ---- Test 10 ----
    [Fact]
    public async Task Cancel_AfterRunAsyncCompletes_NoOp()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var mockOrch = new Mock<IWatchOrchestrator>();
        mockOrch
            .Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSummary());
        var task = new WatchTask(input, () => mockOrch.Object, new FakeTimeProvider(), _silentLogger);

        _ = await task.RunAsync(MakeOptions(dir, NewTempDir()), CancellationToken.None);

        Action act = () => task.Cancel();
        act.Should().NotThrow();
    }

    // ---- Test 11 ----
    [Fact]
    public async Task RunAsync_ConcurrentCancelDuringDispatch_GracefulShutdown()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var entered = new TaskCompletionSource();
        var mockOrch = new Mock<IWatchOrchestrator>();
        mockOrch
            .Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return MakeSummary();
            });
        var task = new WatchTask(input, () => mockOrch.Object, new FakeTimeProvider(), _silentLogger);

        var run = task.RunAsync(MakeOptions(dir, NewTempDir()), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Cancel from a different thread.
        var cancelTask = Task.Run(() => task.Cancel());
        await cancelTask;

        Func<Task> act = () => run;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Test 12.5 (T11) ----
    [Fact]
    public void Cancel_SetsIsDrainingFlag()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var task = new WatchTask(input, () => new Mock<IWatchOrchestrator>().Object, new FakeTimeProvider(), _silentLogger);
        task.IsDraining.Should().BeFalse();

        task.Cancel();

        task.IsDraining.Should().BeTrue();
    }

    // ---- Test 12 ----
    [Fact]
    public async Task RunAsync_OrchestratorReturnsNormally_TaskStaysActive()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var mockOrch = new Mock<IWatchOrchestrator>();
        mockOrch
            .Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSummary(ok: 1, processed: 1));
        var task = new WatchTask(input, () => mockOrch.Object, new FakeTimeProvider(), _silentLogger);

        _ = await task.RunAsync(MakeOptions(dir, NewTempDir()), CancellationToken.None);

        task.State.Should().Be(WatchTaskState.Active);
    }

}
