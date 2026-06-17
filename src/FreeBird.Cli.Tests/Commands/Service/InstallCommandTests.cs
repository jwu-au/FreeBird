using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli.Commands.Service;
using FreeBird.Cli.Service;
using FreeBird.Core.Service;
using Moq;
using Xunit;

namespace FreeBird.Cli.Tests.Commands.Service;

/// <summary>
/// T18 — Behaviour tests for <see cref="InstallCommand.HandleAsync"/>, the OS-agnostic
/// core of <c>fb service install</c>.
///
/// <para>The handler runs entirely against injected, mocked collaborators
/// (<see cref="IElevationChecker"/>, <see cref="IConfigLoader"/>,
/// <see cref="IServiceController"/>, <see cref="IEventLogWriter"/>) plus injected
/// console streams, so it is fully testable on macOS / Linux without ever touching the
/// real Windows Service Control Manager.</para>
///
/// <para>Exit codes (design §): 0 success, 1 not admin, 2 already installed,
/// 3 config invalid/missing, 4 SCM error.</para>
///
/// <para>NOTE on the env-var test (case 5): <c>FB_SERVICE_PASSWORD</c> is a
/// process-global mutable seam. It is set + reset inside a <c>try/finally</c> so it
/// cannot leak across tests. This is NOT one of the production static seams listed in
/// AGENTS.md §6, so no <c>[Collection]</c> is required; the other password test (case 6)
/// deliberately uses the stdin-prompt path to avoid relying on the env var at all.</para>
/// </summary>
public sealed class InstallCommandTests
{
    private static RootConfig BuildConfig(
        string name = "FreeBird",
        string displayName = "FreeBird Cache Decryptor",
        string description = "Decrypts NetEase cache files continuously.",
        RestartPolicy restartPolicy = RestartPolicy.Restart,
        string[]? inputs = null)
    {
        return new RootConfig
        {
            Service = new ServiceConfig
            {
                Name = name,
                DisplayName = displayName,
                Description = description,
                RestartPolicy = restartPolicy,
                LogToEventLog = true,
            },
            Watch = new WatchConfig
            {
                Inputs = inputs ?? new[] { @"D:\Cache\NetEase" },
            },
        };
    }

    private static InstallArgs Args(
        string configPath = @"C:\cfg\config.json",
        string? serviceAccount = null,
        string? servicePassword = null)
    {
        return new InstallArgs(configPath, serviceAccount, servicePassword);
    }

    [Fact]
    public async Task NonAdmin_ReturnsOne_WritesElevationMessage_DoesNotInstall()
    {
        var elevation = new Mock<IElevationChecker>();
        elevation.Setup(e => e.IsAdmin()).Returns(false);
        var configLoader = new Mock<IConfigLoader>();
        var controller = new Mock<IServiceController>();
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await InstallCommand.HandleAsync(
            Args(),
            elevation.Object,
            configLoader.Object,
            controller.Object,
            eventLog.Object,
            stdout,
            stderr,
            TextReader.Null,
            CancellationToken.None);

        exit.Should().Be(1);
        stderr.ToString().Should().Contain(AdminElevationMessage.Build("install", @"C:\cfg\config.json"));
        controller.Verify(c => c.Install(It.IsAny<ServiceInstallRequest>()), Times.Never);
    }

    [Fact]
    public async Task InvalidConfig_ReturnsThree_NamesField_DoesNotInstall()
    {
        var elevation = new Mock<IElevationChecker>();
        elevation.Setup(e => e.IsAdmin()).Returns(true);
        var configLoader = new Mock<IConfigLoader>();
        configLoader
            .Setup(c => c.Load(It.IsAny<string>()))
            .Throws(new ConfigLoadException("watch.inputs", "must not be empty"));
        var controller = new Mock<IServiceController>();
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await InstallCommand.HandleAsync(
            Args(configPath: @"C:\cfg\bad.json"),
            elevation.Object,
            configLoader.Object,
            controller.Object,
            eventLog.Object,
            stdout,
            stderr,
            TextReader.Null,
            CancellationToken.None);

        exit.Should().Be(3);
        var err = stderr.ToString();
        err.Should().Contain("watch.inputs");
        err.Should().Contain(@"C:\cfg\bad.json");
        controller.Verify(c => c.Install(It.IsAny<ServiceInstallRequest>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyInstalled_ReturnsTwo_DoesNotInstall()
    {
        var elevation = new Mock<IElevationChecker>();
        elevation.Setup(e => e.IsAdmin()).Returns(true);
        var configLoader = new Mock<IConfigLoader>();
        configLoader.Setup(c => c.Load(It.IsAny<string>())).Returns(BuildConfig());
        var controller = new Mock<IServiceController>();
        controller.Setup(c => c.Exists("FreeBird")).Returns(true);
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await InstallCommand.HandleAsync(
            Args(),
            elevation.Object,
            configLoader.Object,
            controller.Object,
            eventLog.Object,
            stdout,
            stderr,
            TextReader.Null,
            CancellationToken.None);

        exit.Should().Be(2);
        stderr.ToString().Should().Contain("already installed");
        controller.Verify(c => c.Install(It.IsAny<ServiceInstallRequest>()), Times.Never);
    }

    [Fact]
    public async Task HappyPath_InstallsWithProjectedRequest_CreatesEventSource_ReturnsZero()
    {
        var config = BuildConfig(
            name: "FreeBird",
            displayName: "FreeBird Cache Decryptor",
            description: "Decrypts NetEase cache files continuously.",
            restartPolicy: RestartPolicy.Backoff);
        var elevation = new Mock<IElevationChecker>();
        elevation.Setup(e => e.IsAdmin()).Returns(true);
        var configLoader = new Mock<IConfigLoader>();
        configLoader.Setup(c => c.Load(It.IsAny<string>())).Returns(config);
        var controller = new Mock<IServiceController>();
        controller.Setup(c => c.Exists(It.IsAny<string>())).Returns(false);
        ServiceInstallRequest? captured = null;
        controller
            .Setup(c => c.Install(It.IsAny<ServiceInstallRequest>()))
            .Callback<ServiceInstallRequest>(r => captured = r);
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await InstallCommand.HandleAsync(
            Args(),
            elevation.Object,
            configLoader.Object,
            controller.Object,
            eventLog.Object,
            stdout,
            stderr,
            TextReader.Null,
            CancellationToken.None);

        exit.Should().Be(0);
        controller.Verify(c => c.Install(It.IsAny<ServiceInstallRequest>()), Times.Once);
        captured.Should().NotBeNull();
        captured!.ShortName.Should().Be("FreeBird");
        captured.DisplayName.Should().Be("FreeBird Cache Decryptor");
        captured.Description.Should().Be("Decrypts NetEase cache files continuously.");
        captured.RestartPolicy.Should().Be(RestartPolicy.Backoff);
        captured.Account.Should().Be("LocalSystem");
        eventLog.Verify(e => e.CreateSourceIfMissing("FreeBird", "Application"), Times.Once);
        stdout.ToString().Should().Contain("FreeBird");
    }

    [Fact]
    public async Task ServiceAccount_PasswordFromEnv_IsUsed_AndNeverEchoed()
    {
        const string envPassword = "s3cr3t-from-env";
        var previous = Environment.GetEnvironmentVariable("FB_SERVICE_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("FB_SERVICE_PASSWORD", envPassword);

            var elevation = new Mock<IElevationChecker>();
            elevation.Setup(e => e.IsAdmin()).Returns(true);
            var configLoader = new Mock<IConfigLoader>();
            configLoader.Setup(c => c.Load(It.IsAny<string>())).Returns(BuildConfig());
            var controller = new Mock<IServiceController>();
            controller.Setup(c => c.Exists(It.IsAny<string>())).Returns(false);
            ServiceInstallRequest? captured = null;
            controller
                .Setup(c => c.Install(It.IsAny<ServiceInstallRequest>()))
                .Callback<ServiceInstallRequest>(r => captured = r);
            var eventLog = new Mock<IEventLogWriter>();
            var stdout = new StringWriter();
            var stderr = new StringWriter();

            var exit = await InstallCommand.HandleAsync(
                Args(serviceAccount: @"DOMAIN\svc"),
                elevation.Object,
                configLoader.Object,
                controller.Object,
                eventLog.Object,
                stdout,
                stderr,
                TextReader.Null,
                CancellationToken.None);

            exit.Should().Be(0);
            captured.Should().NotBeNull();
            captured!.Account.Should().Be(@"DOMAIN\svc");
            captured.Password.Should().Be(envPassword);
            stdout.ToString().Should().NotContain(envPassword);
            stderr.ToString().Should().NotContain(envPassword);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FB_SERVICE_PASSWORD", previous);
        }
    }

    [Fact]
    public async Task ServiceAccount_PasswordFromStdinPrompt_IsUsed()
    {
        // Ensure the env path is not taken for this test.
        var previous = Environment.GetEnvironmentVariable("FB_SERVICE_PASSWORD");
        try
        {
            Environment.SetEnvironmentVariable("FB_SERVICE_PASSWORD", null);
            const string typed = "typed-at-prompt";

            var elevation = new Mock<IElevationChecker>();
            elevation.Setup(e => e.IsAdmin()).Returns(true);
            var configLoader = new Mock<IConfigLoader>();
            configLoader.Setup(c => c.Load(It.IsAny<string>())).Returns(BuildConfig());
            var controller = new Mock<IServiceController>();
            controller.Setup(c => c.Exists(It.IsAny<string>())).Returns(false);
            ServiceInstallRequest? captured = null;
            controller
                .Setup(c => c.Install(It.IsAny<ServiceInstallRequest>()))
                .Callback<ServiceInstallRequest>(r => captured = r);
            var eventLog = new Mock<IEventLogWriter>();
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var stdin = new StringReader(typed + "\n");

            var exit = await InstallCommand.HandleAsync(
                Args(serviceAccount: @"DOMAIN\svc"),
                elevation.Object,
                configLoader.Object,
                controller.Object,
                eventLog.Object,
                stdout,
                stderr,
                stdin,
                CancellationToken.None);

            exit.Should().Be(0);
            captured.Should().NotBeNull();
            captured!.Password.Should().Be(typed);
            stdout.ToString().Should().NotContain(typed);
            stderr.ToString().Should().NotContain(typed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FB_SERVICE_PASSWORD", previous);
        }
    }

    [Fact]
    public async Task ScmError_ReturnsFour_WritesScmError()
    {
        var elevation = new Mock<IElevationChecker>();
        elevation.Setup(e => e.IsAdmin()).Returns(true);
        var configLoader = new Mock<IConfigLoader>();
        configLoader.Setup(c => c.Load(It.IsAny<string>())).Returns(BuildConfig());
        var controller = new Mock<IServiceController>();
        controller.Setup(c => c.Exists(It.IsAny<string>())).Returns(false);
        controller
            .Setup(c => c.Install(It.IsAny<ServiceInstallRequest>()))
            .Throws(new InvalidOperationException("SCM refused the registration"));
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await InstallCommand.HandleAsync(
            Args(),
            elevation.Object,
            configLoader.Object,
            controller.Object,
            eventLog.Object,
            stdout,
            stderr,
            TextReader.Null,
            CancellationToken.None);

        exit.Should().Be(4);
        stderr.ToString().Should().Contain("SCM");
    }

    [Fact]
    public async Task UserProfileInput_WithLocalSystem_WarnsButStillInstalls()
    {
        var config = BuildConfig(
            inputs: new[] { @"C:\Users\bob\AppData\Local\NetEase\Cache" });
        var elevation = new Mock<IElevationChecker>();
        elevation.Setup(e => e.IsAdmin()).Returns(true);
        var configLoader = new Mock<IConfigLoader>();
        configLoader.Setup(c => c.Load(It.IsAny<string>())).Returns(config);
        var controller = new Mock<IServiceController>();
        controller.Setup(c => c.Exists(It.IsAny<string>())).Returns(false);
        var eventLog = new Mock<IEventLogWriter>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await InstallCommand.HandleAsync(
            Args(),
            elevation.Object,
            configLoader.Object,
            controller.Object,
            eventLog.Object,
            stdout,
            stderr,
            TextReader.Null,
            CancellationToken.None);

        exit.Should().Be(0);
        controller.Verify(c => c.Install(It.IsAny<ServiceInstallRequest>()), Times.Once);
        var output = stdout.ToString();
        output.Should().Contain("--service-account");
        output.Should().Contain("LocalSystem");
    }

    [Fact]
    public void BuildBinPath_QuotesExeAndConfig()
    {
        var result = InstallCommand.BuildBinPath(
            "C:/Program Files/fb/fb.exe",
            "C:/cfg/config.json");

        result.Should().Be("\"C:/Program Files/fb/fb.exe\" service run --config \"C:/cfg/config.json\"");
    }
}
