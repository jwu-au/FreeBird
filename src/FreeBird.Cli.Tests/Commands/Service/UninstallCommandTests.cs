using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli.Commands.Service;
using FreeBird.Cli.Service;
using Moq;
using Xunit;

namespace FreeBird.Cli.Tests.Commands.Service;

/// <summary>
/// T19 — Behaviour tests for <see cref="UninstallCommand.HandleAsync"/>, the OS-agnostic
/// core of <c>fb service uninstall</c>.
///
/// <para>Order of operations under test (design §): admin-gate → installed-gate →
/// confirmation gate (skippable via <c>--yes</c> or <c>FB_SERVICE_UNINSTALL_YES=1</c>)
/// → stop-if-running → uninstall → remove event-log source. Exit codes: 0 success or
/// aborted-by-user, 1 not admin, 2 not installed, 3 SCM error.</para>
///
/// <para>All SCM/elevation/event-log dependencies are mocked — this never touches the
/// real Service Control Manager, so the suite runs identically on macOS/Linux/Windows.
/// stdin/stdout/stderr are <see cref="StringReader"/>/<see cref="StringWriter"/> so the
/// confirmation prompt and emitted copy are fully assertable.</para>
/// </summary>
public class UninstallCommandTests
{
    private const string ServiceName = "FreeBird";
    private const string EnvKey = "FB_SERVICE_UNINSTALL_YES";

    private static Mock<IElevationChecker> AdminElevation()
    {
        var mock = new Mock<IElevationChecker>();
        mock.Setup(e => e.IsAdmin()).Returns(true);
        return mock;
    }

    private static ServiceStatusInfo StatusWith(ServiceState state) =>
        new(
            ShortName: ServiceName,
            DisplayName: "FreeBird",
            State: state,
            Account: null,
            StartType: null,
            StartedAtUtc: null,
            ConfigPath: null,
            LogFilePath: null);

    private static Mock<IServiceController> InstalledController(ServiceState state = ServiceState.Stopped)
    {
        var mock = new Mock<IServiceController>();
        mock.Setup(c => c.Exists(ServiceName)).Returns(true);
        mock.Setup(c => c.Query(ServiceName)).Returns(StatusWith(state));
        return mock;
    }

    [Fact]
    public async Task NotAdmin_Returns1_StderrHasElevationMessage_UninstallNeverCalled()
    {
        var elevation = new Mock<IElevationChecker>();
        elevation.Setup(e => e.IsAdmin()).Returns(false);
        var controller = new Mock<IServiceController>();
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await UninstallCommand.HandleAsync(
            new UninstallCommand.UninstallArgs(Yes: true),
            elevation.Object,
            controller.Object,
            eventLog.Object,
            TextReader.Null,
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(1);
        stderr.ToString().Should().Contain(AdminElevationMessage.Build("uninstall"));
        controller.Verify(c => c.Uninstall(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task NotInstalled_Returns2_UninstallNeverCalled()
    {
        var elevation = AdminElevation();
        var controller = new Mock<IServiceController>();
        controller.Setup(c => c.Exists(ServiceName)).Returns(false);
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await UninstallCommand.HandleAsync(
            new UninstallCommand.UninstallArgs(Yes: true),
            elevation.Object,
            controller.Object,
            eventLog.Object,
            TextReader.Null,
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(2);
        stderr.ToString().Should().Contain("not installed");
        controller.Verify(c => c.Uninstall(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmPrompt_EmptyInput_Aborts_Returns0_NoMutations()
    {
        var elevation = AdminElevation();
        var controller = InstalledController(ServiceState.Stopped);
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await UninstallCommand.HandleAsync(
            new UninstallCommand.UninstallArgs(Yes: false),
            elevation.Object,
            controller.Object,
            eventLog.Object,
            new StringReader(""),
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(0);
        stdout.ToString().Should().Contain("Aborted");
        controller.Verify(c => c.Uninstall(It.IsAny<string>()), Times.Never);
        controller.Verify(c => c.StopAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        eventLog.Verify(e => e.RemoveSourceIfExists(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmPrompt_DefaultNewline_Aborts_Returns0_NoMutations()
    {
        var elevation = AdminElevation();
        var controller = InstalledController(ServiceState.Stopped);
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await UninstallCommand.HandleAsync(
            new UninstallCommand.UninstallArgs(Yes: false),
            elevation.Object,
            controller.Object,
            eventLog.Object,
            new StringReader("\n"),
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(0);
        controller.Verify(c => c.Uninstall(It.IsAny<string>()), Times.Never);
        eventLog.Verify(e => e.RemoveSourceIfExists(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmPrompt_YInput_Proceeds_UninstallCalledOnce()
    {
        var elevation = AdminElevation();
        var controller = InstalledController(ServiceState.Stopped);
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await UninstallCommand.HandleAsync(
            new UninstallCommand.UninstallArgs(Yes: false),
            elevation.Object,
            controller.Object,
            eventLog.Object,
            new StringReader("y\n"),
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(0);
        controller.Verify(c => c.Uninstall(ServiceName), Times.Once);
    }

    [Fact]
    public async Task YesFlag_SkipsPrompt_UninstallCalled_NoStdinRead()
    {
        var elevation = AdminElevation();
        var controller = InstalledController(ServiceState.Stopped);
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        // TextReader.Null guarantees the handler did not depend on reading any input.
        var exit = await UninstallCommand.HandleAsync(
            new UninstallCommand.UninstallArgs(Yes: true),
            elevation.Object,
            controller.Object,
            eventLog.Object,
            TextReader.Null,
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(0);
        controller.Verify(c => c.Uninstall(ServiceName), Times.Once);
    }

    [Fact]
    public async Task EnvVarSet_SkipsPrompt_UninstallCalled()
    {
        var original = Environment.GetEnvironmentVariable(EnvKey);
        try
        {
            Environment.SetEnvironmentVariable(EnvKey, "1");

            var elevation = AdminElevation();
            var controller = InstalledController(ServiceState.Stopped);
            var eventLog = new Mock<IEventLogWriter>();
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = await UninstallCommand.HandleAsync(
                new UninstallCommand.UninstallArgs(Yes: false),
                elevation.Object,
                controller.Object,
                eventLog.Object,
                TextReader.Null,
                stdout,
                stderr,
                CancellationToken.None);

            exit.Should().Be(0);
            controller.Verify(c => c.Uninstall(ServiceName), Times.Once);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKey, original);
        }
    }

    [Fact]
    public async Task RunningService_StopsBeforeUninstall_ThenRemovesSource_InOrder()
    {
        var elevation = AdminElevation();
        var controller = InstalledController(ServiceState.Running);
        var eventLog = new Mock<IEventLogWriter>();

        var calls = new List<string>();
        controller
            .Setup(c => c.StopAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("Stop"))
            .Returns(Task.CompletedTask);
        controller
            .Setup(c => c.Uninstall(ServiceName))
            .Callback(() => calls.Add("Uninstall"));
        eventLog
            .Setup(e => e.RemoveSourceIfExists(ServiceName))
            .Callback(() => calls.Add("RemoveSource"));

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await UninstallCommand.HandleAsync(
            new UninstallCommand.UninstallArgs(Yes: true),
            elevation.Object,
            controller.Object,
            eventLog.Object,
            TextReader.Null,
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(0);
        calls.Should().Equal("Stop", "Uninstall", "RemoveSource");
    }

    [Fact]
    public async Task StoppedService_DoesNotStop_UninstallsAndRemovesSource()
    {
        var elevation = AdminElevation();
        var controller = InstalledController(ServiceState.Stopped);
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await UninstallCommand.HandleAsync(
            new UninstallCommand.UninstallArgs(Yes: true),
            elevation.Object,
            controller.Object,
            eventLog.Object,
            TextReader.Null,
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(0);
        controller.Verify(c => c.StopAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
        controller.Verify(c => c.Uninstall(ServiceName), Times.Once);
        eventLog.Verify(e => e.RemoveSourceIfExists(ServiceName), Times.Once);
    }

    [Fact]
    public async Task RemoveSource_CalledAfterUninstall()
    {
        var elevation = AdminElevation();
        var controller = InstalledController(ServiceState.Stopped);
        var eventLog = new Mock<IEventLogWriter>();

        var calls = new List<string>();
        controller.Setup(c => c.Uninstall(ServiceName)).Callback(() => calls.Add("Uninstall"));
        eventLog.Setup(e => e.RemoveSourceIfExists(ServiceName)).Callback(() => calls.Add("RemoveSource"));

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await UninstallCommand.HandleAsync(
            new UninstallCommand.UninstallArgs(Yes: true),
            elevation.Object,
            controller.Object,
            eventLog.Object,
            TextReader.Null,
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(0);
        calls.Should().Equal("Uninstall", "RemoveSource");
    }

    [Fact]
    public async Task ScmError_UninstallThrows_Returns3_StderrMentionsScm_NoRemoveSource()
    {
        var elevation = AdminElevation();
        var controller = InstalledController(ServiceState.Stopped);
        controller.Setup(c => c.Uninstall(ServiceName)).Throws(new InvalidOperationException("access denied"));
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await UninstallCommand.HandleAsync(
            new UninstallCommand.UninstallArgs(Yes: true),
            elevation.Object,
            controller.Object,
            eventLog.Object,
            TextReader.Null,
            stdout,
            stderr,
            CancellationToken.None);

        exit.Should().Be(3);
        stderr.ToString().Should().Contain("SCM");
        eventLog.Verify(e => e.RemoveSourceIfExists(It.IsAny<string>()), Times.Never);
    }
}
