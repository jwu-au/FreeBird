using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Watch;

/// <summary>
/// Tests for <see cref="WatchTask"/> — the per-input-dir state machine wrapper
/// around an <see cref="IWatchOrchestrator"/> instance.
///
/// State machine: Initializing → Active → (Dead → Resurrecting → Active) loops.
/// Crash window: 3 crashes within a sliding 60s window forces transition to Dead.
/// </summary>
public sealed class WatchTaskStateMachineTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly Serilog.Core.Logger _silentLogger;

    public WatchTaskStateMachineTests()
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
        var path = Path.Combine(Path.GetTempPath(), $"fb-watchtask-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private string NewNonExistentPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fb-watchtask-missing-{Guid.NewGuid():N}");
        // Do NOT create; just track for cleanup if test ends up creating it.
        _tempDirs.Add(path);
        return path;
    }

    private WatchInput MakeInput(string path) => WatchInput.FromPath(path);

    [Fact]
    public void Constructor_DirectoryExists_StartsActive()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var orchestrator = new Mock<IWatchOrchestrator>().Object;
        var factoryCalls = 0;
        Func<IWatchOrchestrator> factory = () => { factoryCalls++; return orchestrator; };
        var clock = new FakeTimeProvider();

        var task = new WatchTask(input, factory, clock, _silentLogger);

        task.State.Should().Be(WatchTaskState.Active);
        factoryCalls.Should().Be(1);
        task.Input.Should().Be(input);
    }

    [Fact]
    public void Constructor_DirectoryMissing_StartsDead()
    {
        var missing = NewNonExistentPath();
        var input = MakeInput(missing);
        var factoryCalls = 0;
        Func<IWatchOrchestrator> factory = () => { factoryCalls++; return new Mock<IWatchOrchestrator>().Object; };
        var clock = new FakeTimeProvider();

        var task = new WatchTask(input, factory, clock, _silentLogger);

        task.State.Should().Be(WatchTaskState.Dead);
        factoryCalls.Should().Be(0);
    }

    [Fact]
    public void Constructor_FactoryThrows_StartsDead()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        Func<IWatchOrchestrator> factory = () => throw new InvalidOperationException("boom");
        var clock = new FakeTimeProvider();

        var task = new WatchTask(input, factory, clock, _silentLogger);

        task.State.Should().Be(WatchTaskState.Dead);
    }

    [Fact]
    public void Constructor_NullDependencies_Throw()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        Func<IWatchOrchestrator> factory = () => new Mock<IWatchOrchestrator>().Object;
        var clock = new FakeTimeProvider();

        Action a1 = () => new WatchTask(null!, factory, clock, _silentLogger);
        Action a2 = () => new WatchTask(input, null!, clock, _silentLogger);
        Action a3 = () => new WatchTask(input, factory, null!, _silentLogger);
        Action a4 = () => new WatchTask(input, factory, clock, null!);

        a1.Should().Throw<ArgumentNullException>();
        a2.Should().Throw<ArgumentNullException>();
        a3.Should().Throw<ArgumentNullException>();
        a4.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void RecordCrash_OneCrash_ReturnsFalseAndStaysActive()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var factory = (Func<IWatchOrchestrator>)(() => new Mock<IWatchOrchestrator>().Object);
        var clock = new FakeTimeProvider();
        var task = new WatchTask(input, factory, clock, _silentLogger);

        var died = task.RecordCrash(new InvalidOperationException("first"));

        died.Should().BeFalse();
        task.State.Should().Be(WatchTaskState.Active);
    }

    [Fact]
    public void RecordCrash_ThreeCrashesInWindow_ReturnsTrueAndGoesDead()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var factory = (Func<IWatchOrchestrator>)(() => new Mock<IWatchOrchestrator>().Object);
        var clock = new FakeTimeProvider();
        var task = new WatchTask(input, factory, clock, _silentLogger);

        task.RecordCrash(new Exception("1")).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(10));
        task.RecordCrash(new Exception("2")).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(10));
        var died = task.RecordCrash(new Exception("3"));

        died.Should().BeTrue();
        task.State.Should().Be(WatchTaskState.Dead);
    }

    [Fact]
    public void RecordCrash_FourthCrashOutsideWindow_ResetsCounter()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var factory = (Func<IWatchOrchestrator>)(() => new Mock<IWatchOrchestrator>().Object);
        var clock = new FakeTimeProvider();
        var task = new WatchTask(input, factory, clock, _silentLogger);

        // Two crashes close together
        task.RecordCrash(new Exception("1")).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(5));
        task.RecordCrash(new Exception("2")).Should().BeFalse();

        // Advance past 60s window from FIRST crash (eviction window measured from oldest)
        clock.Advance(TimeSpan.FromSeconds(61));

        // This crash should now only see 1 entry in the window (itself), since the two
        // earlier crashes are both older than 60s.
        var died = task.RecordCrash(new Exception("3"));

        died.Should().BeFalse();
        task.State.Should().Be(WatchTaskState.Active);
    }

    [Fact]
    public void TryResurrect_FromActiveState_ReturnsFalse()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var factory = (Func<IWatchOrchestrator>)(() => new Mock<IWatchOrchestrator>().Object);
        var clock = new FakeTimeProvider();
        var task = new WatchTask(input, factory, clock, _silentLogger);

        var result = task.TryResurrect();

        result.Should().BeFalse();
        task.State.Should().Be(WatchTaskState.Active);
    }

    [Fact]
    public void TryResurrect_FromDeadWithMissingDir_ReturnsFalse()
    {
        var missing = NewNonExistentPath();
        var input = MakeInput(missing);
        var factory = (Func<IWatchOrchestrator>)(() => new Mock<IWatchOrchestrator>().Object);
        var clock = new FakeTimeProvider();
        var task = new WatchTask(input, factory, clock, _silentLogger);
        task.State.Should().Be(WatchTaskState.Dead);

        var result = task.TryResurrect();

        result.Should().BeFalse();
        task.State.Should().Be(WatchTaskState.Dead);
    }

    [Fact]
    public void TryResurrect_FromDeadWithExistingDir_Success_GoesActive()
    {
        // Start in DEAD via missing dir, then create the dir, then resurrect.
        var path = NewNonExistentPath();
        var input = MakeInput(path);
        var factory = (Func<IWatchOrchestrator>)(() => new Mock<IWatchOrchestrator>().Object);
        var clock = new FakeTimeProvider();
        var task = new WatchTask(input, factory, clock, _silentLogger);
        task.State.Should().Be(WatchTaskState.Dead);

        // Materialize the directory now.
        Directory.CreateDirectory(path);

        // Preload crash history so we can verify it's cleared on successful resurrection.
        // (Crashes pre-resurrection are valid: they may have been recorded before DEAD.)
        // We can't currently push to crash history in DEAD state via RecordCrash because
        // it would no-op the state transition. Instead, verify behaviour post-resurrect:
        // after resurrect, three new crashes should be required to re-die.
        var result = task.TryResurrect();

        result.Should().BeTrue();
        task.State.Should().Be(WatchTaskState.Active);

        // Verify counter was reset: 2 crashes should NOT push back to DEAD.
        task.RecordCrash(new Exception("1")).Should().BeFalse();
        task.RecordCrash(new Exception("2")).Should().BeFalse();
        task.State.Should().Be(WatchTaskState.Active);
    }

    [Fact]
    public void TryResurrect_FactoryThrows_StaysDead()
    {
        var path = NewNonExistentPath();
        var input = MakeInput(path);
        // Factory: first call (constructor) won't fire because dir is missing → DEAD.
        // After we create the dir and call TryResurrect, factory is invoked and throws.
        Func<IWatchOrchestrator> factory = () => throw new InvalidOperationException("factory failure");
        var clock = new FakeTimeProvider();
        var task = new WatchTask(input, factory, clock, _silentLogger);
        task.State.Should().Be(WatchTaskState.Dead);

        Directory.CreateDirectory(path);

        var result = task.TryResurrect();

        result.Should().BeFalse();
        task.State.Should().Be(WatchTaskState.Dead);
    }

    [Fact]
    public void DemoteIfDirectoryVanished_DirExists_ReturnsFalse()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var factory = (Func<IWatchOrchestrator>)(() => new Mock<IWatchOrchestrator>().Object);
        var clock = new FakeTimeProvider();
        var task = new WatchTask(input, factory, clock, _silentLogger);

        var demoted = task.DemoteIfDirectoryVanished();

        demoted.Should().BeFalse();
        task.State.Should().Be(WatchTaskState.Active);
    }

    [Fact]
    public void DemoteIfDirectoryVanished_DirMissing_GoesDead()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var factory = (Func<IWatchOrchestrator>)(() => new Mock<IWatchOrchestrator>().Object);
        var clock = new FakeTimeProvider();
        var task = new WatchTask(input, factory, clock, _silentLogger);
        task.State.Should().Be(WatchTaskState.Active);

        // Yank the directory out from under the task.
        Directory.Delete(dir, recursive: true);

        var demoted = task.DemoteIfDirectoryVanished();

        demoted.Should().BeTrue();
        task.State.Should().Be(WatchTaskState.Dead);
    }

    [Fact]
    public void State_AccessFromMultipleThreads_DoesNotCorrupt()
    {
        var dir = NewTempDir();
        var input = MakeInput(dir);
        var factory = (Func<IWatchOrchestrator>)(() => new Mock<IWatchOrchestrator>().Object);
        var clock = new FakeTimeProvider();
        var task = new WatchTask(input, factory, clock, _silentLogger);

        // 50 threads. Half read State 100 times each. Half call RecordCrash 5 times each
        // (spread across the window so we may or may not transition to DEAD).
        var threads = new List<Thread>();
        var exceptions = new List<Exception>();
        var exLock = new object();

        for (var i = 0; i < 25; i++)
        {
            threads.Add(new Thread(() =>
            {
                try
                {
                    for (var j = 0; j < 100; j++)
                    {
                        _ = task.State;
                    }
                }
                catch (Exception ex)
                {
                    lock (exLock) { exceptions.Add(ex); }
                }
            }));
        }

        for (var i = 0; i < 25; i++)
        {
            threads.Add(new Thread(() =>
            {
                try
                {
                    for (var j = 0; j < 5; j++)
                    {
                        task.RecordCrash(new Exception("stress"));
                    }
                }
                catch (Exception ex)
                {
                    lock (exLock) { exceptions.Add(ex); }
                }
            }));
        }

        foreach (var t in threads) { t.Start(); }
        foreach (var t in threads) { t.Join(); }

        exceptions.Should().BeEmpty();
        // With 25 threads × 5 crashes = 125 crashes recorded total, definitely > 3,
        // so the task must have transitioned to DEAD at some point.
        task.State.Should().Be(WatchTaskState.Dead);
    }
}
