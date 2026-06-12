using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;
using Xunit;

namespace FreeBird.Core.Tests.Watch;

/// <summary>
/// Tests for <see cref="WatchSupervisor.RunAsync"/> (T09): concurrent fan-out of WatchTasks
/// using <see cref="Task.WhenAll(System.Threading.Tasks.Task[])"/>, graceful drain on external
/// cancellation, per-task crash isolation, and ScanSummary aggregation.
/// </summary>
public sealed class WatchSupervisorRuntimeTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly Serilog.Core.Logger _silentLogger;

    public WatchSupervisorRuntimeTests()
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
        var path = Path.Combine(Path.GetTempPath(), $"fb-watchsup-rt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private string NewNonExistentPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fb-watchsup-rt-missing-{Guid.NewGuid():N}");
        _tempDirs.Add(path);
        return path;
    }

    private WatchSupervisor MakeSupervisor(ILogger? log = null)
    {
        return new WatchSupervisor(
            taskFactory: input => new WatchTask(
                input,
                () => new Mock<IWatchOrchestrator>().Object,
                new FakeTimeProvider(),
                log ?? _silentLogger),
            timeProvider: new FakeTimeProvider(),
            log: log ?? _silentLogger);
    }

    private static WatchOptions MakeOptionsTemplate(string outputDir)
        => new WatchOptions(
            InputDirs: Array.Empty<string>(),
            OutputDir: outputDir);

    private static ScanSummary MakeSummary(
        int ok = 0,
        int processed = 0,
        TimeSpan? duration = null)
        => new ScanSummary(
            Processed: processed,
            Ok: ok,
            Skipped: 0,
            UnknownFormat: 0,
            IntegrityFailed: 0,
            Errors: 0,
            Duration: duration ?? TimeSpan.Zero);

    private WatchTask MakeTaskWithOrchestrator(string dir, IWatchOrchestrator orch)
    {
        var input = WatchInput.FromPath(dir);
        return new WatchTask(input, () => orch, new FakeTimeProvider(), _silentLogger);
    }

    // ---- Test 1 ----
    [Fact]
    public async Task RunAsync_NullTasks_Throws()
    {
        var sup = MakeSupervisor();
        var opts = MakeOptionsTemplate(NewTempDir());

        Func<Task> act = () => sup.RunAsync(null!, opts, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---- Test 2 ----
    [Fact]
    public async Task RunAsync_NullOptions_Throws()
    {
        var sup = MakeSupervisor();

        Func<Task> act = () => sup.RunAsync(Array.Empty<WatchTask>(), null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---- Test 3 ----
    [Fact]
    public async Task RunAsync_EmptyTasks_LogsWarnAndReturnsEmpty()
    {
        var logger = new Mock<ILogger>();
        var sup = MakeSupervisor(logger.Object);
        var opts = MakeOptionsTemplate(NewTempDir());

        var summary = await sup.RunAsync(Array.Empty<WatchTask>(), opts, CancellationToken.None);

        summary.Should().NotBeNull();
        summary.Processed.Should().Be(0);
        summary.Ok.Should().Be(0);
        summary.Duration.Should().Be(TimeSpan.Zero);
        logger.Verify(
            l => l.Warning("watch supervisor: no tasks to run; returning empty summary"),
            Times.Once);
    }

    // ---- Test 4 ----
    [Fact]
    public async Task RunAsync_SingleTask_DelegatesAndReturnsItsSummary()
    {
        var dir = NewTempDir();
        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSummary(ok: 5, processed: 5));
        var task = MakeTaskWithOrchestrator(dir, orch.Object);

        var sup = MakeSupervisor();
        var summary = await sup.RunAsync(new[] { task }, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);

        summary.Ok.Should().Be(5);
        summary.Processed.Should().Be(5);
        orch.Verify(
            o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ---- Test 5 ----
    [Fact]
    public async Task RunAsync_MultipleTasks_AggregatesSummaries()
    {
        var oks = new[] { 1, 2, 3 };
        var tasks = new List<WatchTask>();
        foreach (var ok in oks)
        {
            var dir = NewTempDir();
            var orch = new Mock<IWatchOrchestrator>();
            orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSummary(ok: ok, processed: ok));
            tasks.Add(MakeTaskWithOrchestrator(dir, orch.Object));
        }

        var sup = MakeSupervisor();
        var summary = await sup.RunAsync(tasks, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);

        summary.Ok.Should().Be(6);
        summary.Processed.Should().Be(6);
    }

    // ---- Test 6 ----
    [Fact]
    public async Task RunAsync_AllTasksRunConcurrently()
    {
        // Each orchestrator awaits ~100ms; running them serially would take ~300ms.
        // Parallel fan-out should complete in well under 250ms.
        const int delayMs = 100;
        const int count = 3;
        var tasks = new List<WatchTask>();
        for (int i = 0; i < count; i++)
        {
            var dir = NewTempDir();
            var orch = new Mock<IWatchOrchestrator>();
            orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
                .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                    return MakeSummary(ok: 1);
                });
            tasks.Add(MakeTaskWithOrchestrator(dir, orch.Object));
        }

        var sup = MakeSupervisor();
        var sw = Stopwatch.StartNew();
        var summary = await sup.RunAsync(tasks, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);
        sw.Stop();

        summary.Ok.Should().Be(count);
        sw.ElapsedMilliseconds.Should().BeLessThan(250,
            "parallel fan-out should run all 3 tasks concurrently, not serially");
    }

    // ---- Test 7 ----
    [Fact]
    public async Task RunAsync_PerTaskOptionsHaveOnlyThisInputDir()
    {
        var dirA = NewTempDir();
        var dirB = NewTempDir();
        var captured = new List<WatchOptions>();
        var captureLock = new object();

        Mock<IWatchOrchestrator> MakeCapturingOrch()
        {
            var m = new Mock<IWatchOrchestrator>();
            m.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
                .Returns<WatchOptions, CancellationToken, CancellationToken>((opts, _, _) =>
                {
                    lock (captureLock) { captured.Add(opts); }
                    return Task.FromResult(MakeSummary());
                });
            return m;
        }

        var taskA = MakeTaskWithOrchestrator(dirA, MakeCapturingOrch().Object);
        var taskB = MakeTaskWithOrchestrator(dirB, MakeCapturingOrch().Object);

        var template = MakeOptionsTemplate(NewTempDir()) with { Concurrency = 7 };
        var sup = MakeSupervisor();
        await sup.RunAsync(new[] { taskA, taskB }, template, CancellationToken.None);

        captured.Should().HaveCount(2);
        captured.Should().Contain(o => o.InputDirs.Count == 1 && o.InputDirs[0] == taskA.Input.Path);
        captured.Should().Contain(o => o.InputDirs.Count == 1 && o.InputDirs[0] == taskB.Input.Path);
        // Other knobs (OutputDir, Concurrency) inherited from template
        captured.Should().OnlyContain(o => o.OutputDir == template.OutputDir);
        captured.Should().OnlyContain(o => o.Concurrency == 7);
    }

    // ---- Test 8 ----
    [Fact]
    public async Task RunAsync_ExternalCancellation_DrainsAllTasks()
    {
        // Each orchestrator parks on its CT. When external CT cancels, supervisor should
        // call task.Cancel() on every task, which signals the per-task linked CTS, propagating
        // OCE up through WatchTask.RunAsync. The supervisor swallows it (drain) and returns.
        const int count = 3;
        var entered = new TaskCompletionSource[count];
        for (int i = 0; i < count; i++) entered[i] = new TaskCompletionSource();

        var tasks = new List<WatchTask>();
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            var dir = NewTempDir();
            var orch = new Mock<IWatchOrchestrator>();
            orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
                .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
                {
                    entered[idx].TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    return MakeSummary();
                });
            tasks.Add(MakeTaskWithOrchestrator(dir, orch.Object));
        }

        var sup = MakeSupervisor();
        using var externalCts = new CancellationTokenSource();

        var run = sup.RunAsync(tasks, MakeOptionsTemplate(NewTempDir()), externalCts.Token);

        // Wait until all orchestrators have entered Task.Delay (so per-task linked CTS exists).
        foreach (var tcs in entered) await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        externalCts.Cancel();

        // Supervisor should drain and return without throwing
        var summary = await run.WaitAsync(TimeSpan.FromSeconds(5));
        summary.Should().NotBeNull();
        // No tasks completed their work; aggregate is empty
        summary.Ok.Should().Be(0);
    }

    // ---- Test 9 ----
    [Fact]
    public async Task RunAsync_OneTaskCrashes_OthersComplete()
    {
        // Task 0 returns ok=1, task 1 throws fatally (3 crashes worth, but the WatchTask
        // will rethrow non-fatal; we want a single non-OCE throw to bubble), task 2 returns ok=3.
        // T09 contract: per-task crash MUST be isolated; sibling tasks finish normally.
        var dir0 = NewTempDir();
        var dir1 = NewTempDir();
        var dir2 = NewTempDir();

        var orch0 = new Mock<IWatchOrchestrator>();
        orch0.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSummary(ok: 1));

        var orch1 = new Mock<IWatchOrchestrator>();
        orch1.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("task 1 boom"));

        var orch2 = new Mock<IWatchOrchestrator>();
        orch2.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSummary(ok: 3));

        var tasks = new[]
        {
            MakeTaskWithOrchestrator(dir0, orch0.Object),
            MakeTaskWithOrchestrator(dir1, orch1.Object),
            MakeTaskWithOrchestrator(dir2, orch2.Object),
        };

        var sup = MakeSupervisor();
        var summary = await sup.RunAsync(tasks, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);

        // task 1 contributes empty (isolated crash). Task 0 + 2 contribute Ok=1 + 3 = 4.
        summary.Ok.Should().Be(4);
    }

    // ---- Test 10 ----
    [Fact]
    public async Task RunAsync_BornDeadTasks_StillIncludedAsEmpty()
    {
        // A task constructed for a non-existent dir is born DEAD and returns empty.
        var dirAlive = NewTempDir();
        var dirDead = NewNonExistentPath();

        var orchAlive = new Mock<IWatchOrchestrator>();
        orchAlive.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSummary(ok: 2));

        // Dead orch must NEVER be called.
        var orchDead = new Mock<IWatchOrchestrator>(MockBehavior.Strict);

        var aliveTask = MakeTaskWithOrchestrator(dirAlive, orchAlive.Object);
        var deadTask = MakeTaskWithOrchestrator(dirDead, orchDead.Object);
        deadTask.State.Should().Be(WatchTaskState.Dead);

        var sup = MakeSupervisor();
        var summary = await sup.RunAsync(new[] { aliveTask, deadTask }, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);

        summary.Ok.Should().Be(2);
        orchDead.VerifyNoOtherCalls();
    }

    // ---- Test 11 ----
    [Fact]
    public async Task RunAsync_AllTasksDead_ReturnsEmptyAggregate()
    {
        var orchUnused = new Mock<IWatchOrchestrator>(MockBehavior.Strict);
        var t1 = MakeTaskWithOrchestrator(NewNonExistentPath(), orchUnused.Object);
        var t2 = MakeTaskWithOrchestrator(NewNonExistentPath(), orchUnused.Object);
        t1.State.Should().Be(WatchTaskState.Dead);
        t2.State.Should().Be(WatchTaskState.Dead);

        var sup = MakeSupervisor();
        var summary = await sup.RunAsync(new[] { t1, t2 }, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);

        summary.Should().NotBeNull();
        summary.Ok.Should().Be(0);
        summary.Processed.Should().Be(0);
        summary.Duration.Should().Be(TimeSpan.Zero);
    }

    // ---- Test 12 ----
    [Fact]
    public async Task RunAsync_DurationAggregated()
    {
        // Each task reports a 100ms Duration. Aggregate must SUM (not max/min).
        var per = TimeSpan.FromMilliseconds(100);
        var tasks = new List<WatchTask>();
        for (int i = 0; i < 3; i++)
        {
            var dir = NewTempDir();
            var orch = new Mock<IWatchOrchestrator>();
            orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MakeSummary(duration: per));
            tasks.Add(MakeTaskWithOrchestrator(dir, orch.Object));
        }

        var sup = MakeSupervisor();
        var summary = await sup.RunAsync(tasks, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);

        summary.Duration.Should().Be(TimeSpan.FromMilliseconds(300));
    }

    // ---- Test 12.5 (T11 bug-fix verification) ----
    [Fact]
    public async Task RunAsync_TaskDrainedByProbe_NoSpuriousWarn()
    {
        // Simulate the T11 probe-drain scenario: external CT is NOT cancelled, but a sibling
        // calls task.Cancel() on one of the tasks (mimicking HealthProbe demote). The
        // supervisor's RunOneTaskIsolated must classify the resulting OCE as a benign drain
        // (because task.IsDraining is set) and NOT emit a WARN log.
        var logger = new Mock<ILogger>();
        var dir = NewTempDir();
        var entered = new TaskCompletionSource();
        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return MakeSummary();
            });
        var task = MakeTaskWithOrchestrator(dir, orch.Object);

        var sup = MakeSupervisor(logger.Object);
        using var externalCts = new CancellationTokenSource();
        var run = sup.RunAsync(new[] { task }, MakeOptionsTemplate(NewTempDir()), externalCts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Probe-style drain: directly cancel the task while external CT remains live.
        task.Cancel();
        task.IsDraining.Should().BeTrue();

        var summary = await run.WaitAsync(TimeSpan.FromSeconds(5));
        summary.Should().NotBeNull();

        // The supervisor MUST NOT have logged the WARN "[watch={Base}] task crashed; isolated"
        // for this probe-drain. (Before the T11 OCE-filter widening, this would fail.)
        logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                "[watch={Base}] task crashed; isolated from other tasks",
                It.IsAny<string>()),
            Times.Never);
    }

    // ---- Test 13 ----
    [Fact]
    public async Task RunAsync_LogsStartAndComplete()
    {
        var logger = new Mock<ILogger>();
        var dir = NewTempDir();
        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSummary(ok: 1));
        var task = MakeTaskWithOrchestrator(dir, orch.Object);

        var sup = MakeSupervisor(logger.Object);
        await sup.RunAsync(new[] { task }, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);

        logger.Verify(
            l => l.Information("watch supervisor: starting {Count} tasks", It.IsAny<int>()),
            Times.Once);
        logger.Verify(
            l => l.Information(
                "watch supervisor: all {Count} tasks completed; aggregate Ok={Ok} Failed={Failed}",
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Once);
    }
}
