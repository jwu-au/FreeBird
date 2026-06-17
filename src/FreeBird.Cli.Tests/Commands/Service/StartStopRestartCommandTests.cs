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
/// T20 — Behaviour tests for the OS-agnostic cores of <c>fb service start</c>,
/// <c>fb service stop</c>, and <c>fb service restart</c>
/// (<see cref="StartCommand.HandleAsync"/>, <see cref="StopCommand.HandleAsync"/>,
/// <see cref="RestartCommand.HandleAsync"/>).
///
/// <para>Exit-code contract (design §3, LOCKED):</para>
/// <list type="bullet">
///   <item>start: 0 success, 1 not admin, 2 not installed, 3 already running, 4 start failed.</item>
///   <item>stop: 0 success, 1 not admin, 2 not installed, 3 already stopped, 4 stop failed.</item>
///   <item>restart: 0 success, 1 not admin, 2 not installed, 3 restart failed (never 4).</item>
/// </list>
///
/// <para>All SCM/elevation dependencies are mocked — this never touches the real Service
/// Control Manager, so the suite runs identically on macOS/Linux/Windows. stdout/stderr are
/// <see cref="StringWriter"/> so emitted copy is fully assertable.</para>
/// </summary>
public class StartStopRestartCommandTests
{
    private const string ServiceName = "FreeBird";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static Mock<IElevationChecker> Elevation(bool isAdmin)
    {
        var mock = new Mock<IElevationChecker>();
        mock.Setup(e => e.IsAdmin()).Returns(isAdmin);
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

    private static Mock<IServiceController> InstalledController(ServiceState state)
    {
        var mock = new Mock<IServiceController>();
        mock.Setup(c => c.Exists(ServiceName)).Returns(true);
        mock.Setup(c => c.Query(ServiceName)).Returns(StatusWith(state));
        return mock;
    }

    private static Mock<IServiceController> NotInstalledController()
    {
        var mock = new Mock<IServiceController>();
        mock.Setup(c => c.Exists(ServiceName)).Returns(false);
        return mock;
    }

    // ----------------------------------------------------------------------------------
    // StartCommand
    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task Start_NotAdmin_Returns1_StartAsyncNeverCalled()
    {
        var elevation = Elevation(isAdmin: false);
        var controller = InstalledController(ServiceState.Stopped);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await StartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(1);
        stderr.ToString().Should().Contain("requires Administrator privileges");
        controller.Verify(
            c => c.StartAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Start_NotInstalled_Returns2_StartAsyncNeverCalled()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = NotInstalledController();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await StartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(2);
        stderr.ToString().Should().Contain("not installed");
        controller.Verify(
            c => c.StartAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Start_AlreadyRunning_Returns3_StartAsyncNeverCalled()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Running);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await StartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(3);
        stderr.ToString().Should().Contain("already running");
        controller.Verify(
            c => c.StartAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Start_StartAsyncThrows_Returns4_StderrMentionsStartFailed()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Stopped);
        controller
            .Setup(c => c.StartAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("scm boom"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await StartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(4);
        stderr.ToString().Should().Contain("start failed");
        stderr.ToString().Should().Contain("scm boom");
    }

    [Fact]
    public async Task Start_Happy_Returns0_StartAsyncCalledOnce()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Stopped);
        controller
            .Setup(c => c.StartAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await StartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(0);
        controller.Verify(
            c => c.StartAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ----------------------------------------------------------------------------------
    // StopCommand
    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_NotAdmin_Returns1_StopAsyncNeverCalled()
    {
        var elevation = Elevation(isAdmin: false);
        var controller = InstalledController(ServiceState.Running);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await StopCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(1);
        stderr.ToString().Should().Contain("requires Administrator privileges");
        controller.Verify(
            c => c.StopAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Stop_NotInstalled_Returns2_StopAsyncNeverCalled()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = NotInstalledController();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await StopCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(2);
        stderr.ToString().Should().Contain("not installed");
        controller.Verify(
            c => c.StopAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Stop_AlreadyStopped_Returns3_StopAsyncNeverCalled()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Stopped);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await StopCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(3);
        stderr.ToString().Should().Contain("already stopped");
        controller.Verify(
            c => c.StopAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Stop_StopAsyncThrows_Returns4_StderrMentionsStopFailed()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Running);
        controller
            .Setup(c => c.StopAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("scm boom"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await StopCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(4);
        stderr.ToString().Should().Contain("stop failed");
        stderr.ToString().Should().Contain("scm boom");
    }

    [Fact]
    public async Task Stop_TimeoutPropagatedToStopAsync()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Running);
        controller
            .Setup(c => c.StopAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var timeout = TimeSpan.FromSeconds(10);

        var exit = await StopCommand.HandleAsync(
            ServiceName, timeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(0);
        controller.Verify(
            c => c.StopAsync(ServiceName, It.Is<TimeSpan>(t => t == TimeSpan.FromSeconds(10)), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Stop_Happy_Returns0_StopAsyncCalledOnce()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Running);
        controller
            .Setup(c => c.StopAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await StopCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(0);
        controller.Verify(
            c => c.StopAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ----------------------------------------------------------------------------------
    // RestartCommand
    // ----------------------------------------------------------------------------------

    [Fact]
    public async Task Restart_NotAdmin_Returns1_NoScmCalls()
    {
        var elevation = Elevation(isAdmin: false);
        var controller = InstalledController(ServiceState.Running);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RestartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(1);
        stderr.ToString().Should().Contain("requires Administrator privileges");
        controller.Verify(
            c => c.StopAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        controller.Verify(
            c => c.StartAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Restart_NotInstalled_Returns2()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = NotInstalledController();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RestartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(2);
        stderr.ToString().Should().Contain("not installed");
    }

    [Fact]
    public async Task Restart_Running_StopsThenStarts_InOrder_Returns0()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Running);
        var callOrder = new List<string>();
        controller
            .Setup(c => c.StopAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => callOrder.Add("Stop"));
        controller
            .Setup(c => c.StartAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => callOrder.Add("Start"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RestartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(0);
        callOrder.Should().Equal("Stop", "Start");
    }

    [Fact]
    public async Task Restart_StopFails_Returns3_StopPhase_StartNeverCalled()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Running);
        controller
            .Setup(c => c.StopAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("stop boom"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RestartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(3);
        stderr.ToString().Should().Contain("restart failed: stop phase");
        stderr.ToString().Should().Contain("stop boom");
        controller.Verify(
            c => c.StartAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Restart_StartFailsAfterGoodStop_Returns3_StartPhase()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Running);
        controller
            .Setup(c => c.StopAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        controller
            .Setup(c => c.StartAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("start boom"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RestartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(3);
        stderr.ToString().Should().Contain("restart failed: start phase");
        stderr.ToString().Should().Contain("start boom");
    }

    [Fact]
    public async Task Restart_AlreadyStopped_SkipsStop_StartsOnly_Returns0()
    {
        var elevation = Elevation(isAdmin: true);
        var controller = InstalledController(ServiceState.Stopped);
        controller
            .Setup(c => c.StartAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RestartCommand.HandleAsync(
            ServiceName, DefaultTimeout, elevation.Object, controller.Object, stdout, stderr, CancellationToken.None);

        exit.Should().Be(0);
        controller.Verify(
            c => c.StopAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Never);
        controller.Verify(
            c => c.StartAsync(ServiceName, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
