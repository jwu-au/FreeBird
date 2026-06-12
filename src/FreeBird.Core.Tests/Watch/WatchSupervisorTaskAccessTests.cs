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
/// T10 tests: WatchSupervisor task access helpers used by HealthProbe (T11).
/// Exercises GetManagedTasks / GetDeadTasks / GetActiveTasks lifecycle and thread-safety.
/// All accessors are read-only; mutation of WatchTask state remains on WatchTask itself.
/// </summary>
public sealed class WatchSupervisorTaskAccessTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly Serilog.Core.Logger _silentLogger;

    public WatchSupervisorTaskAccessTests()
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
        var path = Path.Combine(Path.GetTempPath(), $"fb-watchsup-access-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private WatchSupervisor MakeSupervisor()
    {
        return new WatchSupervisor(
            taskFactory: input => new WatchTask(
                input,
                () => new Mock<IWatchOrchestrator>().Object,
                new FakeTimeProvider(),
                _silentLogger),
            timeProvider: new FakeTimeProvider(),
            log: _silentLogger);
    }

    private static WatchOptions MakeOptionsTemplate(string outputDir)
        => new WatchOptions(InputDirs: Array.Empty<string>(), OutputDir: outputDir);

    private static ScanSummary EmptySummary()
        => new ScanSummary(0, 0, 0, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Build a WatchTask whose RunAsync awaits <paramref name="gate"/> before completing.
    /// Lets the test inspect supervisor state while RunAsync is in flight.
    /// </summary>
    private WatchTask MakeGatedTask(string dir, Task gate)
    {
        var orch = new Mock<IWatchOrchestrator>();
        orch.Setup(o => o.RunAsync(It.IsAny<WatchOptions>(), It.IsAny<CancellationToken>(), It.IsAny<CancellationToken>()))
            .Returns<WatchOptions, CancellationToken, CancellationToken>(async (_, ct, _) =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                return EmptySummary();
            });
        var input = WatchInput.FromPath(dir);
        return new WatchTask(input, () => orch.Object, new FakeTimeProvider(), _silentLogger);
    }

    /// <summary>
    /// Build a WatchTask whose orchestrator immediately throws to drive the task to DEAD.
    /// Used together with crash threshold (3 crashes) to land in Dead state.
    /// </summary>
    private WatchTask MakeBornDeadTask(string dir)
    {
        // A non-existent directory makes WatchInput.FromPath set ExistedAtStartup=false → born-DEAD.
        var bogusPath = Path.Combine(Path.GetTempPath(), $"fb-watchsup-access-missing-{Guid.NewGuid():N}");
        _tempDirs.Add(bogusPath);
        var input = WatchInput.FromPath(bogusPath);
        return new WatchTask(input, () => new Mock<IWatchOrchestrator>().Object, new FakeTimeProvider(), _silentLogger);
    }

    // ---- Test 1 ----
    [Fact]
    public void GetManagedTasks_BeforeRunAsync_ReturnsEmpty()
    {
        var sup = MakeSupervisor();

        var snapshot = sup.GetManagedTasks();

        snapshot.Should().NotBeNull();
        snapshot.Should().BeEmpty();
    }

    // ---- Test 2 ----
    [Fact]
    public async Task GetManagedTasks_DuringRunAsync_ReturnsTasks()
    {
        var sup = MakeSupervisor();
        var gate = new TaskCompletionSource();
        var tasks = new[]
        {
            MakeGatedTask(NewTempDir(), gate.Task),
            MakeGatedTask(NewTempDir(), gate.Task),
            MakeGatedTask(NewTempDir(), gate.Task),
        };
        var opts = MakeOptionsTemplate(NewTempDir());

        var runTask = sup.RunAsync(tasks, opts, CancellationToken.None);

        // Poll for in-flight state — runAsync stores tasks synchronously before awaiting.
        IReadOnlyList<WatchTask> snapshot = Array.Empty<WatchTask>();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            snapshot = sup.GetManagedTasks();
            if (snapshot.Count == 3) { break; }
            await Task.Delay(10);
        }

        snapshot.Should().HaveCount(3);
        snapshot.Should().BeEquivalentTo(tasks);

        gate.SetResult();
        await runTask;
    }

    // ---- Test 3 ----
    [Fact]
    public async Task GetManagedTasks_AfterRunAsync_ReturnsEmpty()
    {
        var sup = MakeSupervisor();
        var gate = new TaskCompletionSource();
        var tasks = new[] { MakeGatedTask(NewTempDir(), gate.Task) };
        var opts = MakeOptionsTemplate(NewTempDir());

        var runTask = sup.RunAsync(tasks, opts, CancellationToken.None);
        gate.SetResult();
        await runTask;

        sup.GetManagedTasks().Should().BeEmpty();
    }

    // ---- Test 4 ----
    [Fact]
    public void GetDeadTasks_BeforeRun_ReturnsEmpty()
    {
        var sup = MakeSupervisor();

        sup.GetDeadTasks().Should().BeEmpty();
    }

    // ---- Test 5 ----
    [Fact]
    public async Task GetDeadTasks_DuringRun_FiltersByState()
    {
        var sup = MakeSupervisor();
        var gate = new TaskCompletionSource();
        var activeTask = MakeGatedTask(NewTempDir(), gate.Task);
        var deadTask1 = MakeBornDeadTask(NewTempDir());
        var deadTask2 = MakeBornDeadTask(NewTempDir());
        var tasks = new[] { activeTask, deadTask1, deadTask2 };
        var opts = MakeOptionsTemplate(NewTempDir());

        // Sanity precondition: born-DEAD tasks really are DEAD.
        deadTask1.State.Should().Be(WatchTaskState.Dead);
        deadTask2.State.Should().Be(WatchTaskState.Dead);

        var runTask = sup.RunAsync(tasks, opts, CancellationToken.None);

        IReadOnlyList<WatchTask> dead = Array.Empty<WatchTask>();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            dead = sup.GetDeadTasks();
            if (dead.Count == 2) { break; }
            await Task.Delay(10);
        }

        dead.Should().HaveCount(2);
        dead.Should().BeEquivalentTo(new[] { deadTask1, deadTask2 });
        dead.Should().NotContain(activeTask);

        gate.SetResult();
        await runTask;
    }

    // ---- Test 6 ----
    [Fact]
    public async Task GetActiveTasks_DuringRun_FiltersByState()
    {
        var sup = MakeSupervisor();
        var gate = new TaskCompletionSource();
        var activeTask1 = MakeGatedTask(NewTempDir(), gate.Task);
        var activeTask2 = MakeGatedTask(NewTempDir(), gate.Task);
        var deadTask = MakeBornDeadTask(NewTempDir());
        var tasks = new[] { activeTask1, activeTask2, deadTask };
        var opts = MakeOptionsTemplate(NewTempDir());

        var runTask = sup.RunAsync(tasks, opts, CancellationToken.None);

        IReadOnlyList<WatchTask> active = Array.Empty<WatchTask>();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            active = sup.GetActiveTasks();
            if (active.Count == 2) { break; }
            await Task.Delay(10);
        }

        active.Should().HaveCount(2);
        active.Should().BeEquivalentTo(new[] { activeTask1, activeTask2 });
        active.Should().NotContain(deadTask);

        gate.SetResult();
        await runTask;
    }

    // ---- Test 7 ----
    [Fact]
    public async Task GetManagedTasks_ConcurrentReadDuringRunAsync_ThreadSafe()
    {
        var sup = MakeSupervisor();
        var gate = new TaskCompletionSource();
        var tasks = new[]
        {
            MakeGatedTask(NewTempDir(), gate.Task),
            MakeGatedTask(NewTempDir(), gate.Task),
            MakeGatedTask(NewTempDir(), gate.Task),
            MakeGatedTask(NewTempDir(), gate.Task),
        };
        var opts = MakeOptionsTemplate(NewTempDir());

        var runTask = sup.RunAsync(tasks, opts, CancellationToken.None);

        // Wait for tasks to land in the supervisor.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (sup.GetManagedTasks().Count != 4 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        // 50 threads hammer the accessor.
        var readers = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                var snap = sup.GetManagedTasks();
                snap.Should().HaveCount(4);
            }
        })).ToArray();

        var readFault = await Record.ExceptionAsync(() => Task.WhenAll(readers));
        readFault.Should().BeNull();

        gate.SetResult();
        await runTask;
    }

    // ---- Test 8 ----
    [Fact]
    public async Task GetManagedTasks_ReturnsSnapshot_NotLive()
    {
        var sup = MakeSupervisor();
        var gate = new TaskCompletionSource();
        var tasks = new[] { MakeGatedTask(NewTempDir(), gate.Task) };
        var opts = MakeOptionsTemplate(NewTempDir());

        var runTask = sup.RunAsync(tasks, opts, CancellationToken.None);

        // Wait for in-flight state.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (sup.GetManagedTasks().Count != 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        var snapshot = sup.GetManagedTasks();
        snapshot.Should().HaveCount(1);

        // Complete RunAsync — internal storage clears, but our snapshot must be decoupled.
        gate.SetResult();
        await runTask;

        snapshot.Should().HaveCount(1, "the returned list must be a snapshot, not a live reference to internal storage");
        sup.GetManagedTasks().Should().BeEmpty();
    }

    // ---- Test 9 ----
    [Fact]
    public async Task GetDeadTasks_StateChangesDuringEnumeration_DoesNotThrow()
    {
        var sup = MakeSupervisor();
        var gate = new TaskCompletionSource();
        var activeTask = MakeGatedTask(NewTempDir(), gate.Task);
        var deadTask = MakeBornDeadTask(NewTempDir());
        var tasks = new[] { activeTask, deadTask };
        var opts = MakeOptionsTemplate(NewTempDir());

        var runTask = sup.RunAsync(tasks, opts, CancellationToken.None);

        // Wait for fan-out.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (sup.GetManagedTasks().Count != 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }

        using var stop = new CancellationTokenSource();
        // Continuously call GetDeadTasks while a parallel thread flips activeTask state via Cancel().
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = sup.GetDeadTasks();
                _ = sup.GetActiveTasks();
            }
        });
        var mutator = Task.Run(() =>
        {
            // Cancel cycles _taskCts, doesn't directly flip State to Dead, but exercises concurrent
            // State reads/writes against the accessor's enumeration.
            for (int i = 0; i < 200 && !stop.IsCancellationRequested; i++)
            {
                activeTask.Cancel();
            }
        });

        await Task.Delay(50);
        stop.Cancel();
        var readerFault = await Record.ExceptionAsync(() => reader);
        var mutatorFault = await Record.ExceptionAsync(() => mutator);
        readerFault.Should().BeNull();
        mutatorFault.Should().BeNull();

        gate.SetResult();
        await runTask;
    }
}
