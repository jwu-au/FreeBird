using System;
using System.IO;
using FluentAssertions;
using FreeBird.Cli.Commands.Service;
using FreeBird.Cli.Service;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit;

namespace FreeBird.Cli.Tests.Commands.Service;

/// <summary>
/// T21 — Unit tests for <see cref="StatusCommand"/>: the OS-agnostic core of
/// <c>fb service status</c>. The handler reads the SCM through the injected
/// <see cref="IServiceController"/> and renders via the already-tested
/// <see cref="StatusFormatter"/> (T16), so these tests focus on the handler's own
/// control flow: the not-installed short-circuit and the State→exit-code mapping.
///
/// <para>Status does NOT require admin, so the handler signature deliberately omits any
/// elevation dependency — proven at compile time by the absence of an IElevationChecker
/// parameter in every call below.</para>
///
/// <para>Channel decision: the human-readable status block and the "not installed" message
/// both go to <c>stdout</c> (the spec says status "prints"). <c>stderr</c> is reserved for
/// unexpected error diagnostics, of which status has none on the tested paths.</para>
/// </summary>
public class StatusCommandTests
{
    private const string ServiceName = "FreeBird";

    private static ServiceStatusInfo InfoWith(ServiceState state, DateTimeOffset? startedAtUtc = null)
    {
        return new ServiceStatusInfo(
            ShortName: ServiceName,
            DisplayName: "FreeBird Music Cache Decoder",
            State: state,
            Account: "LocalSystem",
            StartType: "Auto",
            StartedAtUtc: startedAtUtc,
            ConfigPath: "C:\\ProgramData\\FreeBird\\config.json",
            LogFilePath: "C:\\ProgramData\\FreeBird\\logs\\watch.log");
    }

    [Fact]
    public void Handle_NotInstalled_ReturnsExit1_PrintsNotInstalled_AndNeverQueries()
    {
        var controller = new Mock<IServiceController>(MockBehavior.Strict);
        controller.Setup(c => c.Exists(ServiceName)).Returns(false);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var clock = new FakeTimeProvider();

        var exit = StatusCommand.Handle(ServiceName, controller.Object, clock, stdout, stderr);

        exit.Should().Be(1);
        (stdout.ToString() + stderr.ToString()).Should().Contain("not installed");
        controller.Verify(c => c.Query(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Handle_Running_ReturnsExit0_AndRendersStatusBlock()
    {
        var startedAtUtc = new DateTimeOffset(2026, 6, 15, 12, 34, 56, TimeSpan.Zero);
        var controller = new Mock<IServiceController>(MockBehavior.Strict);
        controller.Setup(c => c.Exists(ServiceName)).Returns(true);
        controller.Setup(c => c.Query(ServiceName)).Returns(InfoWith(ServiceState.Running, startedAtUtc));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var clock = new FakeTimeProvider();
        // Deterministic uptime: started + 2h14m.
        clock.SetUtcNow(startedAtUtc + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(14));

        var exit = StatusCommand.Handle(ServiceName, controller.Object, clock, stdout, stderr);

        exit.Should().Be(0);
        var output = stdout.ToString();
        output.Should().Contain("Status:");
        output.Should().Contain("Running");
        output.Should().Contain(ServiceName);
        stderr.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Handle_Stopped_ReturnsExit2()
    {
        var controller = new Mock<IServiceController>(MockBehavior.Strict);
        controller.Setup(c => c.Exists(ServiceName)).Returns(true);
        controller.Setup(c => c.Query(ServiceName)).Returns(InfoWith(ServiceState.Stopped));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var clock = new FakeTimeProvider();

        var exit = StatusCommand.Handle(ServiceName, controller.Object, clock, stdout, stderr);

        exit.Should().Be(2);
    }

    [Theory]
    [InlineData(ServiceState.Paused)]
    [InlineData(ServiceState.StartPending)]
    [InlineData(ServiceState.StopPending)]
    [InlineData(ServiceState.Other)]
    public void Handle_TransitionalAndPausedStates_ReturnExit3(ServiceState state)
    {
        var controller = new Mock<IServiceController>(MockBehavior.Strict);
        controller.Setup(c => c.Exists(ServiceName)).Returns(true);
        controller.Setup(c => c.Query(ServiceName)).Returns(InfoWith(state));

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var clock = new FakeTimeProvider();

        var exit = StatusCommand.Handle(ServiceName, controller.Object, clock, stdout, stderr);

        exit.Should().Be(3);
    }
}
