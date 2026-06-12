using System;
using System.Collections.Generic;
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
/// Tests for <see cref="HealthProbe"/> (T11). Covers constructor validation, single-tick
/// demote/resurrect behavior, and the long-running <see cref="HealthProbe.RunAsync"/> loop
/// driven by <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class HealthProbeTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly Serilog.Core.Logger _silentLogger;

    public HealthProbeTests()
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
        var path = Path.Combine(Path.GetTempPath(), $"fb-healthprobe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private string NewNonExistentPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fb-healthprobe-missing-{Guid.NewGuid():N}");
        _tempDirs.Add(path);
        return path;
    }

    private WatchSupervisor MakeSupervisor(ILogger? log = null)
        => new WatchSupervisor(
            taskFactory: input => new WatchTask(
                input,
                () => new Mock<IWatchOrchestrator>().Object,
                new FakeTimeProvider(),
                log ?? _silentLogger),
            timeProvider: new FakeTimeProvider(),
            log: log ?? _silentLogger);

    private WatchTask MakeTaskWithOrchestrator(string dir, IWatchOrchestrator orch)
    {
        var input = WatchInput.FromPath(dir);
        return new WatchTask(input, () => orch, new FakeTimeProvider(), _silentLogger);
    }

    private static WatchOptions MakeOptionsTemplate(string outputDir)
        => new WatchOptions(
            InputDirs: Array.Empty<string>(),
            OutputDir: outputDir);

    private static ScanSummary EmptySummary() => new ScanSummary(
        Processed: 0, Ok: 0, Skipped: 0, UnknownFormat: 0,
        IntegrityFailed: 0, Errors: 0, Duration: TimeSpan.Zero);

    // ---- Constructor validation ----

    [Fact]
    public void Constructor_NullSupervisor_Throws()
    {
        Action act = () => new HealthProbe(null!, new FakeTimeProvider(), _silentLogger, TimeSpan.FromMinutes(5));
        act.Should().Throw<ArgumentNullException>().WithParameterName("supervisor");
    }

    [Fact]
    public void Constructor_NullTimeProvider_Throws()
    {
        var sup = MakeSupervisor();
        Action act = () => new HealthProbe(sup, null!, _silentLogger, TimeSpan.FromMinutes(5));
        act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var sup = MakeSupervisor();
        Action act = () => new HealthProbe(sup, new FakeTimeProvider(), null!, TimeSpan.FromMinutes(5));
        act.Should().Throw<ArgumentNullException>().WithParameterName("log");
    }

    [Fact]
    public void Constructor_ZeroInterval_Throws()
    {
        var sup = MakeSupervisor();
        Action act = () => new HealthProbe(sup, new FakeTimeProvider(), _silentLogger, TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("interval");
    }

    [Fact]
    public void Constructor_NegativeInterval_Throws()
    {
        var sup = MakeSupervisor();
        Action act = () => new HealthProbe(sup, new FakeTimeProvider(), _silentLogger, TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("interval");
    }

    // ---- TickOnce — no managed tasks ----

    [Fact]
    public void TickOnce_NoManagedTasks_NoThrow()
    {
        var sup = MakeSupervisor();
        var probe = new HealthProbe(sup, new FakeTimeProvider(), _silentLogger, TimeSpan.FromMinutes(5));

        Action act = () => probe.TickOnce();
        act.Should().NotThrow();
    }

    // ---- TickOnce — demote vanished ACTIVE task ----

    [Fact]
    public async Task TickOnce_DirectoryVanished_DemotesAndCancels()
    {
        var dir = NewTempDir();
        var entered = new TaskCompletionSource();
        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return EmptySummary();
            });

        var task = MakeTaskWithOrchestrator(dir, orch.Object);
        task.State.Should().Be(WatchTaskState.Active);
        var sup = MakeSupervisor();
        var probe = new HealthProbe(sup, new FakeTimeProvider(), _silentLogger, TimeSpan.FromMinutes(5));

        // Start the supervisor so the task is "managed".
        var supRun = sup.RunAsync(new[] { task }, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Now make the directory vanish.
        Directory.Delete(dir, recursive: true);

        probe.TickOnce();

        // Supervisor should drain without spurious WARN (T09-reviewer bug-fix).
        var summary = await supRun.WaitAsync(TimeSpan.FromSeconds(5));
        summary.Should().NotBeNull();
        task.State.Should().Be(WatchTaskState.Dead);
        task.IsDraining.Should().BeTrue();
    }

    // ---- TickOnce — resurrect DEAD task ----

    [Fact]
    public async Task TickOnce_DeadTaskDirectoryReappeared_Resurrects()
    {
        // Born-DEAD task: dir doesn't exist at construction.
        var missing = NewNonExistentPath();
        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptySummary());
        var task = MakeTaskWithOrchestrator(missing, orch.Object);
        task.State.Should().Be(WatchTaskState.Dead);

        // Now the directory reappears.
        Directory.CreateDirectory(missing);

        // We need the supervisor to expose this DEAD task via GetDeadTasks(), which only
        // returns results while a RunAsync is in flight. Use a live sibling to keep the
        // supervisor's run open while we tick the probe.
        var siblingDir = NewTempDir();
        var siblingEntered = new TaskCompletionSource();
        var siblingOrch = new Mock<IWatchOrchestrator>();
        siblingOrch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                siblingEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return EmptySummary();
            });
        var sibling = MakeTaskWithOrchestrator(siblingDir, siblingOrch.Object);

        var sup = MakeSupervisor();
        var probe = new HealthProbe(sup, new FakeTimeProvider(), _silentLogger, TimeSpan.FromMinutes(5));

        using var externalCts = new CancellationTokenSource();
        var supRun = sup.RunAsync(new[] { task, sibling }, MakeOptionsTemplate(NewTempDir()), externalCts.Token);
        await siblingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While the sibling holds the supervisor open, fire the probe tick.
        probe.TickOnce();

        task.State.Should().Be(WatchTaskState.Active);

        // Cleanup.
        externalCts.Cancel();
        await supRun.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- TickOnce — happy state (no changes) ----

    [Fact]
    public async Task TickOnce_HappyState_NoChanges()
    {
        var dir = NewTempDir();
        var entered = new TaskCompletionSource();
        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return EmptySummary();
            });

        var task = MakeTaskWithOrchestrator(dir, orch.Object);
        var sup = MakeSupervisor();
        var probe = new HealthProbe(sup, new FakeTimeProvider(), _silentLogger, TimeSpan.FromMinutes(5));

        using var externalCts = new CancellationTokenSource();
        var supRun = sup.RunAsync(new[] { task }, MakeOptionsTemplate(NewTempDir()), externalCts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        probe.TickOnce();

        task.State.Should().Be(WatchTaskState.Active);
        task.IsDraining.Should().BeFalse();

        externalCts.Cancel();
        await supRun.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- TickOnce — multiple vanished ----

    [Fact]
    public async Task TickOnce_MultipleVanished_DemotesAll()
    {
        const int count = 3;
        var dirs = new string[count];
        var tasks = new WatchTask[count];
        var entered = new TaskCompletionSource[count];
        for (int i = 0; i < count; i++)
        {
            int idx = i;
            dirs[i] = NewTempDir();
            entered[i] = new TaskCompletionSource();
            var orch = new Mock<IWatchOrchestrator>();
            orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
                .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
                {
                    entered[idx].TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    return EmptySummary();
                });
            tasks[i] = MakeTaskWithOrchestrator(dirs[i], orch.Object);
        }

        var sup = MakeSupervisor();
        var probe = new HealthProbe(sup, new FakeTimeProvider(), _silentLogger, TimeSpan.FromMinutes(5));
        var supRun = sup.RunAsync(tasks, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);
        foreach (var tcs in entered) await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        for (int i = 0; i < count; i++) Directory.Delete(dirs[i], recursive: true);

        probe.TickOnce();

        var summary = await supRun.WaitAsync(TimeSpan.FromSeconds(5));
        summary.Should().NotBeNull();
        foreach (var t in tasks)
        {
            t.State.Should().Be(WatchTaskState.Dead);
            t.IsDraining.Should().BeTrue();
        }
    }

    // ---- RunAsync — cancellation exits cleanly ----

    [Fact]
    public async Task RunAsync_CancellationRequested_ExitsCleanly()
    {
        var sup = MakeSupervisor();
        var fakeTime = new FakeTimeProvider();
        var probe = new HealthProbe(sup, fakeTime, _silentLogger, TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();

        var run = probe.RunAsync(cts.Token);
        cts.Cancel();

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        run.IsCompletedSuccessfully.Should().BeTrue();
    }

    // ---- RunAsync — pre-cancelled token exits without ticking ----

    [Fact]
    public async Task RunAsync_PreCancelledToken_ExitsImmediately()
    {
        var sup = MakeSupervisor();
        var probe = new HealthProbe(sup, new FakeTimeProvider(), _silentLogger, TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await probe.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- RunAsync — ticks at configured interval via FakeTimeProvider ----

    [Fact]
    public async Task RunAsync_TicksAtConfiguredInterval()
    {
        // We can't easily count TickOnce calls without a seam, but we CAN observe side effects:
        // place a task in the supervisor whose directory vanishes; after one interval, the
        // probe should have demoted it.
        var dir = NewTempDir();
        var entered = new TaskCompletionSource();
        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return EmptySummary();
            });
        var task = MakeTaskWithOrchestrator(dir, orch.Object);
        var sup = MakeSupervisor();
        var fakeTime = new FakeTimeProvider(startDateTime: DateTimeOffset.UtcNow);
        var interval = TimeSpan.FromMinutes(5);
        var probe = new HealthProbe(sup, fakeTime, _silentLogger, interval);

        var supRun = sup.RunAsync(new[] { task }, MakeOptionsTemplate(NewTempDir()), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var probeCts = new CancellationTokenSource();
        var probeRun = probe.RunAsync(probeCts.Token);

        // Vanish dir, then advance virtual time past one interval.
        Directory.Delete(dir, recursive: true);
        fakeTime.Advance(interval);

        // The supervisor should drain (probe-triggered) — wait for it.
        var summary = await supRun.WaitAsync(TimeSpan.FromSeconds(5));
        summary.Should().NotBeNull();
        task.State.Should().Be(WatchTaskState.Dead);
        task.IsDraining.Should().BeTrue();

        // Stop the probe.
        probeCts.Cancel();
        await probeRun.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // ---- RunAsync — logs start and stop ----

    [Fact]
    public async Task RunAsync_LogsStartAndStop()
    {
        var logger = new Mock<ILogger>();
        var sup = MakeSupervisor(logger.Object);
        var probe = new HealthProbe(sup, new FakeTimeProvider(), logger.Object, TimeSpan.FromMinutes(5));
        using var cts = new CancellationTokenSource();

        var run = probe.RunAsync(cts.Token);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        logger.Verify(
            l => l.Information("health probe: started; interval={Interval}", It.IsAny<TimeSpan>()),
            Times.Once);
        logger.Verify(
            l => l.Information("health probe: stopped"),
            Times.Once);
    }

    // ---- Resurrect resets IsDraining ----

    [Fact]
    public void TryResurrect_ResetsIsDrainingFlag()
    {
        var dir = NewTempDir();
        var orch = new Mock<IWatchOrchestrator>();
        var task = MakeTaskWithOrchestrator(dir, orch.Object);
        task.State.Should().Be(WatchTaskState.Active);

        // Vanish + demote + cancel sets IsDraining.
        Directory.Delete(dir, recursive: true);
        task.DemoteIfDirectoryVanished().Should().BeTrue();
        task.Cancel();
        task.IsDraining.Should().BeTrue();

        // Bring the directory back and resurrect.
        Directory.CreateDirectory(dir);
        task.TryResurrect().Should().BeTrue();

        task.State.Should().Be(WatchTaskState.Active);
        task.IsDraining.Should().BeFalse();
    }

    // ---- Integration: HealthProbe end-to-end with supervisor (vanish + reappear) ----

    [Fact]
    public async Task RunAsync_Integration_VanishThenReappear_TaskCyclesDeadActive()
    {
        // Build a task whose orchestrator just parks; manipulate the dir + advance time.
        var dir = NewTempDir();
        var entered = new TaskCompletionSource();
        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return EmptySummary();
            });
        var task = MakeTaskWithOrchestrator(dir, orch.Object);

        // A sibling task to keep the supervisor's run alive after the first one drains.
        var siblingDir = NewTempDir();
        var siblingEntered = new TaskCompletionSource();
        var siblingOrch = new Mock<IWatchOrchestrator>();
        siblingOrch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                siblingEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return EmptySummary();
            });
        var sibling = MakeTaskWithOrchestrator(siblingDir, siblingOrch.Object);

        var sup = MakeSupervisor();
        var fakeTime = new FakeTimeProvider(startDateTime: DateTimeOffset.UtcNow);
        var interval = TimeSpan.FromMinutes(5);
        var probe = new HealthProbe(sup, fakeTime, _silentLogger, interval);

        using var externalCts = new CancellationTokenSource();
        var supRun = sup.RunAsync(new[] { task, sibling }, MakeOptionsTemplate(NewTempDir()), externalCts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await siblingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var probeRun = probe.RunAsync(externalCts.Token);

        // Phase 1: vanish 'task' dir; advance one interval; probe should demote it.
        Directory.Delete(dir, recursive: true);
        fakeTime.Advance(interval);

        // Wait until task transitions to DEAD.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (task.State != WatchTaskState.Dead && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        task.State.Should().Be(WatchTaskState.Dead);

        // Phase 2: recreate dir; advance another interval; probe should resurrect.
        Directory.CreateDirectory(dir);
        fakeTime.Advance(interval);

        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (task.State != WatchTaskState.Active && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        task.State.Should().Be(WatchTaskState.Active);
        task.IsDraining.Should().BeFalse();  // reset on resurrect

        // Cleanup.
        externalCts.Cancel();
        await Task.WhenAll(supRun, probeRun).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
