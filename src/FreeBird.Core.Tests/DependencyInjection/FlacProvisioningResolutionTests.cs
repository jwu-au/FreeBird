using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using FreeBird.Core.DependencyInjection;
using FreeBird.Core.Provisioning;
using Serilog;

namespace FreeBird.Core.Tests.DependencyInjection;

/// <summary>
/// T07.5 — verify the IFlacBinaryResolver dependency graph is fully resolvable
/// from CoreModule alone (on top of the host-provided ILogger).
///
/// Before T07.5, FlacBinaryResolver was auto-scanned as an IDependency but its
/// transitive deps (IFileSystem, IPathEnvironment, IAppBaseDirectoryProvider,
/// FlacResolverOptions) had no registrations — so any consumer that actually
/// requested IFlacBinaryResolver crashed at resolve time. These tests pin the
/// minimal wiring that closes that gap.
/// </summary>
public class FlacProvisioningResolutionTests
{
    private static IContainer BuildContainer()
    {
        var b = new ContainerBuilder();
        b.RegisterModule<CoreModule>();
        // Host must register ILogger; tests use a silent one (matches CoreModuleTests pattern)
        var logger = new LoggerConfiguration().CreateLogger();
        b.RegisterInstance<ILogger>(logger).SingleInstance();
        return b.Build();
    }

    [Fact]
    public void IFlacBinaryResolver_ResolvesFromCoreModule()
    {
        using var c = BuildContainer();
        var r = c.Resolve<IFlacBinaryResolver>();
        r.Should().NotBeNull();
        r.Should().BeOfType<FlacBinaryResolver>();
    }

    // Note: the unconditional IFlacAutoInstaller_ResolvesAsNoOp test was removed
    // because it failed on Windows runners (where CoreModule resolves
    // WindowsFlacAutoInstaller instead). The macOS/Linux assertion is preserved
    // by IFlacAutoInstaller_OnMacOS_RemainsNoOp below; the Windows assertion is
    // covered by IFlacAutoInstaller_OnWindows_ResolvesAsWindowsInstaller.

    [Fact]
    public void FlacResolverOptions_HasNonEmptyAppBaseDirectory()
    {
        using var c = BuildContainer();
        var opts = c.Resolve<FlacResolverOptions>();
        opts.AppBaseDirectory.Should().NotBeNullOrEmpty();
        opts.FlacBinOverride.Should().BeNull();
        opts.AutoInstallUrl.Should().BeNull();
        opts.DisableAutoInstall.Should().BeFalse();
    }

    [Fact]
    public void IPathEnvironment_Resolves()
    {
        using var c = BuildContainer();
        c.Resolve<IPathEnvironment>().Should().BeOfType<PathEnvironment>();
    }

    [Fact]
    public void IAppBaseDirectoryProvider_Resolves()
    {
        using var c = BuildContainer();
        c.Resolve<IAppBaseDirectoryProvider>().Should().BeOfType<AppContextBaseDirectoryProvider>();
    }

    [Fact]
    public void IFileSystem_Resolves()
    {
        using var c = BuildContainer();
        c.Resolve<System.IO.Abstractions.IFileSystem>().Should().BeOfType<System.IO.Abstractions.FileSystem>();
    }

    [Fact]
    public async Task ResolvedResolver_ReturnsWithoutThrowing_OnRealEnvironment()
    {
        // App base dir won't have flac next to it in test bin/; PATH may or may not hit;
        // NoOp installer returns NotSupported on Mac/Linux. We do NOT assert on availability —
        // a brew-installed flac on dev machines would yield Path, CI would yield NotFound.
        // The contract under test is: the full graph resolves and the call completes.
        using var c = BuildContainer();
        var r = c.Resolve<IFlacBinaryResolver>();
        var result = await r.ResolveFlacAsync(CancellationToken.None);
        (result.Provenance == FlacBinaryProvenance.Path
         || result.Provenance == FlacBinaryProvenance.NotFound).Should().BeTrue(
            $"resolver should return Path or NotFound on a vanilla environment, got {result.Provenance}");
    }

    [Fact]
    public void FlacBinaryResolver_IsInstancePerLifetimeScope_NotSingleton()
    {
        // FlacBinaryResolver is picked up by the IDependency auto-scan, which uses
        // InstancePerLifetimeScope by default. Sanity-check that no rogue SingleInstance
        // override sneaked in.
        using var c = BuildContainer();
        using var s1 = c.BeginLifetimeScope();
        using var s2 = c.BeginLifetimeScope();
        var r1 = s1.Resolve<IFlacBinaryResolver>();
        var r2 = s2.Resolve<IFlacBinaryResolver>();
        r1.Should().NotBeSameAs(r2);
    }

    // --- T15 — IHttpDownloader + OS-conditional installer registration ---

    [Fact]
    public void IHttpDownloader_ResolvesAsHttpClientDownloader()
    {
        // T15: CoreModule explicitly registers HttpClientDownloader as IHttpDownloader so
        // WindowsFlacAutoInstaller's constructor can satisfy its dependency on Windows.
        // The type is NOT marked IDependency, so this test pins the explicit registration.
        using var c = BuildContainer();
        c.Resolve<IHttpDownloader>().Should().BeOfType<HttpClientDownloader>();
    }

    [Fact]
    public void IHttpDownloader_IsSingleInstance()
    {
        // HttpClientDownloader owns its HttpClient; SingleInstance scoping ensures one
        // wrapped HttpClient + Dispose at container shutdown.
        using var c = BuildContainer();
        var a = c.Resolve<IHttpDownloader>();
        var b = c.Resolve<IHttpDownloader>();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void IFlacAutoInstaller_OnMacOS_RemainsNoOp()
    {
        // T15 guard: the OS-conditional swap MUST NOT trigger on macOS / Linux dev boxes.
        // On Windows the swap installs WindowsFlacAutoInstaller; we cannot exercise that
        // branch from a macOS test runner so we assert the negative.
        if (System.OperatingSystem.IsWindows())
        {
            return;  // Windows path is verified by the WindowsAutoInstallE2ETests SkippableFact
        }
        using var c = BuildContainer();
        c.Resolve<IFlacAutoInstaller>().Should().BeOfType<NoOpFlacAutoInstaller>();
    }

    [Fact]
    public void IFlacAutoInstaller_OnWindows_ResolvesAsWindowsInstaller()
    {
        // T15 positive guard: on Windows, CoreModule must swap the NoOp default
        // for the real WindowsFlacAutoInstaller. Skipped on macOS/Linux where
        // IFlacAutoInstaller_OnMacOS_RemainsNoOp asserts the negative branch.
        if (!System.OperatingSystem.IsWindows())
        {
            return;
        }
        using var c = BuildContainer();
        c.Resolve<IFlacAutoInstaller>().Should().BeOfType<WindowsFlacAutoInstaller>();
    }
}
