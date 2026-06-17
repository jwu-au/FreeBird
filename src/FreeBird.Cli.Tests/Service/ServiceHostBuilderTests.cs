using System;
using Autofac;
using FluentAssertions;
using FreeBird.Cli.Service;
using FreeBird.Core.Provisioning;
using FreeBird.Core.Service;
using FreeBird.Core.Watch;
using Serilog;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T13 — tests for <see cref="ServiceHostBuilder"/>, the service-mode Autofac container
/// builder that mirrors <c>WatchRunner.BuildContainer</c>.
///
/// Pins down:
///   (1) the container builds on the current OS (non-Windows selects the
///       NotSupported* impls without throwing);
///   (2) FlacResolverOptions honors the <c>watch.flac.*</c> config overrides;
///   (3) CoreModule singletons resolve exactly once from the container root.
/// </summary>
public class ServiceHostBuilderTests
{
    private static ILogger NewLogger() => new LoggerConfiguration().CreateLogger();

    [Fact]
    public void BuildContainer_OnCurrentOS_ResolvesServiceController()
    {
        var config = new RootConfig();
        using var container = ServiceHostBuilder.BuildContainer(NewLogger(), config);
        using var scope = container.BeginLifetimeScope();

        scope.Resolve<IServiceController>().Should().NotBeNull();
    }

    [Fact]
    public void BuildContainer_HonorsFlacConfigOverrides()
    {
        var config = new RootConfig
        {
            Watch = new WatchConfig
            {
                Flac = new FlacConfig
                {
                    Bin = "/custom/flac",
                    Url = "http://x",
                    DisableAutoInstall = true,
                },
            },
        };

        using var container = ServiceHostBuilder.BuildContainer(NewLogger(), config);

        var options = container.Resolve<FlacResolverOptions>();

        options.FlacBinOverride.Should().Be("/custom/flac");
        options.AutoInstallUrl.Should().Be("http://x");
        options.DisableAutoInstall.Should().BeTrue();
        options.MetaflacBinOverride.Should().BeNull();
        options.AppBaseDirectory.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void BuildContainer_FlacConfigNull_LeavesOverridesUnset()
    {
        var config = new RootConfig { Watch = new WatchConfig { Flac = null } };

        using var container = ServiceHostBuilder.BuildContainer(NewLogger(), config);

        var options = container.Resolve<FlacResolverOptions>();

        options.FlacBinOverride.Should().BeNull();
        options.AutoInstallUrl.Should().BeNull();
        options.DisableAutoInstall.Should().BeFalse();
    }

    [Fact]
    public void GlobalApiRateLimiter_IsSingleInstance()
    {
        var config = new RootConfig();
        using var container = ServiceHostBuilder.BuildContainer(NewLogger(), config);

        var first = container.Resolve<IGlobalApiRateLimiter>();
        var second = container.Resolve<IGlobalApiRateLimiter>();

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void OutputPathMutexPool_IsSingleInstance()
    {
        var config = new RootConfig();
        using var container = ServiceHostBuilder.BuildContainer(NewLogger(), config);

        var first = container.Resolve<IOutputPathMutexPool>();
        var second = container.Resolve<IOutputPathMutexPool>();

        ReferenceEquals(first, second).Should().BeTrue();
    }
}
