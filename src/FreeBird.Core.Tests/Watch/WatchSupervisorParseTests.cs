using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Watch;

/// <summary>
/// Tests for <see cref="WatchSupervisor"/> — parsing + INITIALIZING fan-out only (T08).
/// RunAsync lifecycle is added in T09 and tested separately.
/// </summary>
public sealed class WatchSupervisorParseTests : IDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly Serilog.Core.Logger _silentLogger;

    public WatchSupervisorParseTests()
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
        var path = Path.Combine(Path.GetTempPath(), $"fb-supervisor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    private WatchSupervisor MakeSupervisor(
        Func<WatchInput, WatchTask>? taskFactory = null,
        ILogger? logger = null)
    {
        taskFactory ??= (input => MakeRealWatchTask(input, logger ?? _silentLogger));
        return new WatchSupervisor(
            taskFactory,
            new FakeTimeProvider(),
            logger ?? _silentLogger);
    }

    private WatchTask MakeRealWatchTask(WatchInput input, ILogger log)
    {
        // Use a stub orchestrator factory; the task itself doesn't need to run for T08 tests.
        Func<IWatchOrchestrator> orchFactory = () => new Mock<IWatchOrchestrator>().Object;
        return new WatchTask(input, orchFactory, new FakeTimeProvider(), log);
    }

    [Fact]
    public void Constructor_NullTaskFactory_Throws()
    {
        Action act = () => new WatchSupervisor(null!, new FakeTimeProvider(), _silentLogger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("taskFactory");
    }

    [Fact]
    public void Constructor_NullTimeProvider_Throws()
    {
        Action act = () => new WatchSupervisor(_ => MakeRealWatchTask(_, _silentLogger), null!, _silentLogger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("timeProvider");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Action act = () => new WatchSupervisor(_ => MakeRealWatchTask(_, _silentLogger), new FakeTimeProvider(), null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("log");
    }

    [Fact]
    public void ParseAndValidateInputs_NullList_Throws()
    {
        var supervisor = MakeSupervisor();
        Action act = () => supervisor.ParseAndValidateInputs(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ParseAndValidateInputs_EmptyList_ReturnsEmpty()
    {
        var supervisor = MakeSupervisor();

        var (valid, invalid) = supervisor.ParseAndValidateInputs(Array.Empty<string>());

        valid.Should().BeEmpty();
        invalid.Should().BeEmpty();
    }

    [Fact]
    public void ParseAndValidateInputs_SingleValid_ReturnsOne()
    {
        var dir = NewTempDir();
        var supervisor = MakeSupervisor();

        var (valid, invalid) = supervisor.ParseAndValidateInputs(new[] { dir });

        valid.Should().HaveCount(1);
        invalid.Should().BeEmpty();
        valid[0].Path.Should().Be(Path.GetFullPath(dir).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    [Fact]
    public void ParseAndValidateInputs_MultiValid_ReturnsAll()
    {
        var d1 = NewTempDir();
        var d2 = NewTempDir();
        var d3 = NewTempDir();
        var supervisor = MakeSupervisor();

        var (valid, invalid) = supervisor.ParseAndValidateInputs(new[] { d1, d2, d3 });

        valid.Should().HaveCount(3);
        invalid.Should().BeEmpty();
    }

    [Fact]
    public void ParseAndValidateInputs_EmptyString_GoesToInvalid()
    {
        var supervisor = MakeSupervisor();

        var (valid, invalid) = supervisor.ParseAndValidateInputs(new[] { string.Empty });

        valid.Should().BeEmpty();
        invalid.Should().HaveCount(1);
        invalid[0].Should().Be(string.Empty);
    }

    [Fact]
    public void ParseAndValidateInputs_WhitespaceString_GoesToInvalid()
    {
        var supervisor = MakeSupervisor();

        var (valid, invalid) = supervisor.ParseAndValidateInputs(new[] { "   " });

        valid.Should().BeEmpty();
        invalid.Should().HaveCount(1);
    }

    [Fact]
    public void ParseAndValidateInputs_NullEntry_GoesToInvalid()
    {
        var supervisor = MakeSupervisor();

        var (valid, invalid) = supervisor.ParseAndValidateInputs(new string?[] { null }!);

        valid.Should().BeEmpty();
        invalid.Should().HaveCount(1);
        invalid[0].Should().Be(string.Empty);
    }

    [Fact]
    public void ParseAndValidateInputs_DuplicateCanonical_DropsButDoesNotCountAsInvalid()
    {
        var dir = NewTempDir();
        // Add trailing separator variant — should be deduped against the bare form.
        var dirWithSlash = dir + Path.DirectorySeparatorChar;
        var supervisor = MakeSupervisor();

        var (valid, invalid) = supervisor.ParseAndValidateInputs(new[] { dir, dirWithSlash });

        valid.Should().HaveCount(1);
        invalid.Should().BeEmpty(); // dupes are dropped, NOT counted invalid
    }

    [Fact]
    public void ParseAndValidateInputs_DifferentRelativePaths_SameCanonical_Deduped()
    {
        var dir = NewTempDir();
        // Compose a redundant absolute form: <dir>/./
        var redundant = Path.Combine(dir, ".");
        var supervisor = MakeSupervisor();

        var (valid, invalid) = supervisor.ParseAndValidateInputs(new[] { dir, redundant });

        valid.Should().HaveCount(1);
        invalid.Should().BeEmpty();
    }

    [Fact]
    public void CreateTasks_NullList_Throws()
    {
        var supervisor = MakeSupervisor();
        Action act = () => supervisor.CreateTasks(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateTasks_HappyPath_CreatesOnePerInput()
    {
        var d1 = NewTempDir();
        var d2 = NewTempDir();
        var d3 = NewTempDir();

        var factoryCalls = 0;
        Func<WatchInput, WatchTask> factory = input =>
        {
            factoryCalls++;
            return MakeRealWatchTask(input, _silentLogger);
        };
        var supervisor = MakeSupervisor(factory);

        var (valid, _) = supervisor.ParseAndValidateInputs(new[] { d1, d2, d3 });
        var tasks = supervisor.CreateTasks(valid);

        tasks.Should().HaveCount(3);
        factoryCalls.Should().Be(3);
    }

    [Fact]
    public void CreateTasks_FactoryReturnsTasksInOrder()
    {
        var d1 = NewTempDir();
        var d2 = NewTempDir();
        var supervisor = MakeSupervisor();

        var (valid, _) = supervisor.ParseAndValidateInputs(new[] { d1, d2 });
        var tasks = supervisor.CreateTasks(valid);

        tasks.Should().HaveCount(2);
        tasks[0].Input.Path.Should().Be(valid[0].Path);
        tasks[1].Input.Path.Should().Be(valid[1].Path);
    }

    [Fact]
    public void CreateTasks_LogsStateAfterCreation()
    {
        var dir = NewTempDir();
        var logger = new Mock<ILogger>();

        // Configure mock so chained .ForContext / etc don't NRE (not used here, but defensive).
        var supervisor = new WatchSupervisor(
            input => MakeRealWatchTask(input, _silentLogger),
            new FakeTimeProvider(),
            logger.Object);

        var (valid, _) = supervisor.ParseAndValidateInputs(new[] { dir });
        var tasks = supervisor.CreateTasks(valid);

        tasks.Should().HaveCount(1);
        // Serilog's ILogger.Information(string, T1, T2) is a real interface method (not an
        // extension) — Moq sees it directly. We verify it was called with the per-task created
        // message during CreateTasks fan-out.
        logger.Verify(
            l => l.Information(
                "[watch={Base}] task created in state {State}",
                It.IsAny<string>(),
                It.IsAny<WatchTaskState>()),
            Times.Once);
    }
}
