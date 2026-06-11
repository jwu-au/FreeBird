using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Provisioning;
using Moq;
using Serilog;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T14 — InstallFlacRunner tests. Substitutes a mock IFlacAutoInstaller via
/// <see cref="InstallFlacRunner.ContainerOverride"/> so no real install runs.
/// Shares <c>ConsoleRedirect</c> collection with other runner tests to prevent
/// races on <c>Console.Out</c>.
/// </summary>
[Collection("ConsoleRedirect")]
public class InstallFlacRunnerTests : IDisposable
{
    public void Dispose() => InstallFlacRunner.ContainerOverride = null;

    private static void OverrideInstaller(Func<IFlacAutoInstaller> installerFactory)
    {
        InstallFlacRunner.ContainerOverride = logger =>
        {
            var builder = new ContainerBuilder();
            builder.RegisterInstance(logger).As<ILogger>().SingleInstance();
            builder.RegisterInstance(installerFactory()).As<IFlacAutoInstaller>().SingleInstance();
            return builder.Build();
        };
    }

    [Fact]
    public async Task Run_DefaultArgs_InvokesInstaller_WithBaseDirAndPinnedUrl()
    {
        string? capturedTarget = null;
        string? capturedUrl = null;
        var installer = new Mock<IFlacAutoInstaller>();
        installer
            .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((t, u, _) => { capturedTarget = t; capturedUrl = u; })
            .ReturnsAsync(new FlacInstallResult.Installed("/fake/flac", "/fake/metaflac"));
        OverrideInstaller(() => installer.Object);

        var exit = await InstallFlacRunner.RunAsync(null, null, false, false, CancellationToken.None);

        exit.Should().Be(InstallFlacCommand.ExitOk);
        capturedTarget.Should().Be(AppContext.BaseDirectory);
        capturedUrl.Should().Be(InstallFlacRunner.DefaultUrl);
    }

    [Fact]
    public async Task Run_CustomTargetAndUrl_PassedThrough()
    {
        string? capturedTarget = null;
        string? capturedUrl = null;
        var installer = new Mock<IFlacAutoInstaller>();
        installer
            .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((t, u, _) => { capturedTarget = t; capturedUrl = u; })
            .ReturnsAsync(new FlacInstallResult.Installed("/custom/dir/flac", "/custom/dir/metaflac"));
        OverrideInstaller(() => installer.Object);

        var exit = await InstallFlacRunner.RunAsync(
            "/custom/dir",
            "https://example.org/flac.zip",
            false, false, CancellationToken.None);

        exit.Should().Be(InstallFlacCommand.ExitOk);
        capturedTarget.Should().Be("/custom/dir");
        capturedUrl.Should().Be("https://example.org/flac.zip");
    }

    [Fact]
    public async Task Run_InstallerReturnsNotSupported_ExitOk()
    {
        var installer = new Mock<IFlacAutoInstaller>();
        installer
            .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FlacInstallResult.NotSupported.Instance);
        OverrideInstaller(() => installer.Object);

        var exit = await InstallFlacRunner.RunAsync(null, null, false, false, CancellationToken.None);

        exit.Should().Be(InstallFlacCommand.ExitOk);
    }

    [Fact]
    public async Task Run_InstallerReturnsFailed_ExitFailed()
    {
        var installer = new Mock<IFlacAutoInstaller>();
        installer
            .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FlacInstallResult.Failed("SHA mismatch"));
        OverrideInstaller(() => installer.Object);

        var exit = await InstallFlacRunner.RunAsync(null, null, false, false, CancellationToken.None);

        exit.Should().Be(InstallFlacCommand.ExitFailed);
    }

    [Fact]
    public async Task Run_InstallerReturnsDisabled_ExitFailed()
    {
        var installer = new Mock<IFlacAutoInstaller>();
        installer
            .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(FlacInstallResult.Disabled.Instance);
        OverrideInstaller(() => installer.Object);

        var exit = await InstallFlacRunner.RunAsync(null, null, false, false, CancellationToken.None);

        exit.Should().Be(InstallFlacCommand.ExitFailed);
    }

    [Fact]
    public async Task Run_InvalidUrl_ExitBadArgs_NoInstallerCall()
    {
        var installer = new Mock<IFlacAutoInstaller>();
        OverrideInstaller(() => installer.Object);

        var exit = await InstallFlacRunner.RunAsync(null, "not-a-url", false, false, CancellationToken.None);

        exit.Should().Be(InstallFlacCommand.ExitBadArgs);
        installer.Verify(
            i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_VerboseAndQuiet_ExitBadArgs_NoInstallerCall()
    {
        var installer = new Mock<IFlacAutoInstaller>();
        OverrideInstaller(() => installer.Object);

        var exit = await InstallFlacRunner.RunAsync(null, null, verbose: true, quiet: true, CancellationToken.None);

        exit.Should().Be(InstallFlacCommand.ExitBadArgs);
        installer.Verify(
            i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_InstallerThrows_ExitFailed_NotCrash()
    {
        var installer = new Mock<IFlacAutoInstaller>();
        installer
            .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        OverrideInstaller(() => installer.Object);

        var exit = await InstallFlacRunner.RunAsync(null, null, false, false, CancellationToken.None);

        exit.Should().Be(InstallFlacCommand.ExitFailed);
    }

    [Fact]
    public async Task Run_Cancelled_ReturnsExitFailed()
    {
        var installer = new Mock<IFlacAutoInstaller>();
        installer
            .Setup(i => i.InstallAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        OverrideInstaller(() => installer.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exit = await InstallFlacRunner.RunAsync(null, null, false, false, cts.Token);

        exit.Should().Be(InstallFlacCommand.ExitFailed);
    }
}
