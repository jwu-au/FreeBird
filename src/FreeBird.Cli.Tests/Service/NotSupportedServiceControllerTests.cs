using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli.Service;
using FreeBird.Core.DependencyInjection;
using FreeBird.Core.Service;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T09 — Behaviour + contract tests for <see cref="NotSupportedServiceController"/>.
///
/// These tests pin down:
///   (1) every <see cref="IServiceController"/> method throws
///       <see cref="PlatformNotSupportedException"/> with the shared friendly message
///       containing both the literal pointer <c>'fb service' is Windows-only.</c> and
///       <c>See README §Service</c> so users on macOS/Linux get a clean error;
///   (2) async methods (<c>StartAsync</c>/<c>StopAsync</c>) surface the exception
///       through <c>await</c> just like a synchronous throw would;
///   (3) the type implements <see cref="IServiceController"/> so it is substitutable
///       at the DI boundary (final wiring in T14);
///   (4) the type is <c>sealed</c> to lock down extensibility;
///   (5) the type lives in the Cli assembly, not Core (B-5/B-6 invariant —
///       service-controller impls stay in Cli and are never assembly-scanned by
///       <see cref="CoreModule"/>).
/// </summary>
public class NotSupportedServiceControllerTests
{
    private const string WindowsOnlyFragment = "'fb service' is Windows-only.";
    private const string ReadmePointerFragment = "See README §Service";

    private static ServiceInstallRequest SampleRequest() => new(
        ShortName: "freebird",
        DisplayName: "FreeBird",
        Description: "desc",
        BinPath: "/usr/local/bin/fb",
        Account: null,
        Password: null,
        RestartPolicy: RestartPolicy.Manual);

    [Fact]
    public void Install_Throws_PlatformNotSupported_WithFriendlyMessage()
    {
        IServiceController sut = new NotSupportedServiceController();
        Action act = () => sut.Install(SampleRequest());

        act.Should().Throw<PlatformNotSupportedException>()
            .Which.Message.Should().Contain(WindowsOnlyFragment).And.Contain(ReadmePointerFragment);
    }

    [Fact]
    public void Uninstall_Throws_PlatformNotSupported_WithFriendlyMessage()
    {
        IServiceController sut = new NotSupportedServiceController();
        Action act = () => sut.Uninstall("freebird");

        act.Should().Throw<PlatformNotSupportedException>()
            .Which.Message.Should().Contain(WindowsOnlyFragment).And.Contain(ReadmePointerFragment);
    }

    [Fact]
    public async Task StartAsync_Throws_PlatformNotSupported_WithFriendlyMessage()
    {
        IServiceController sut = new NotSupportedServiceController();
        Func<Task> act = () => sut.StartAsync("freebird", TimeSpan.FromSeconds(5), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PlatformNotSupportedException>();
        ex.Which.Message.Should().Contain(WindowsOnlyFragment).And.Contain(ReadmePointerFragment);
    }

    [Fact]
    public async Task StopAsync_Throws_PlatformNotSupported_WithFriendlyMessage()
    {
        IServiceController sut = new NotSupportedServiceController();
        Func<Task> act = () => sut.StopAsync("freebird", TimeSpan.FromSeconds(5), CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PlatformNotSupportedException>();
        ex.Which.Message.Should().Contain(WindowsOnlyFragment).And.Contain(ReadmePointerFragment);
    }

    [Fact]
    public void Query_Throws_PlatformNotSupported_WithFriendlyMessage()
    {
        IServiceController sut = new NotSupportedServiceController();
        Action act = () => sut.Query("freebird");

        act.Should().Throw<PlatformNotSupportedException>()
            .Which.Message.Should().Contain(WindowsOnlyFragment).And.Contain(ReadmePointerFragment);
    }

    [Fact]
    public void Exists_Throws_PlatformNotSupported_WithFriendlyMessage()
    {
        IServiceController sut = new NotSupportedServiceController();
        Action act = () => sut.Exists("freebird");

        act.Should().Throw<PlatformNotSupportedException>()
            .Which.Message.Should().Contain(WindowsOnlyFragment).And.Contain(ReadmePointerFragment);
    }

    [Fact]
    public void NotSupportedServiceController_ImplementsIServiceController()
    {
        typeof(IServiceController).IsAssignableFrom(typeof(NotSupportedServiceController))
            .Should().BeTrue("NotSupportedServiceController must be substitutable at the DI boundary in T14");
    }

    [Fact]
    public void NotSupportedServiceController_IsSealed()
    {
        typeof(NotSupportedServiceController).IsSealed
            .Should().BeTrue("the non-Windows stub has no extension points; T14 swaps the impl at DI-binding time");
    }

    [Fact]
    public void NotSupportedServiceController_IsCliAssembly_NotCore()
    {
        typeof(NotSupportedServiceController).Assembly
            .Should().NotBeSameAs(typeof(CoreModule).Assembly,
                "NotSupportedServiceController must live in FreeBird.Cli so CoreModule's assembly scan never auto-registers it");
    }
}
