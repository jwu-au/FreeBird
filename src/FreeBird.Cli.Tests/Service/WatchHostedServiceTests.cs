using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli.Service;
using FreeBird.Core.Models;
using FreeBird.Core.Service;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T13 — tests for <see cref="WatchHostedService"/>.
///
/// <see cref="WatchSupervisor"/> is sealed with a non-virtual <c>RunAsync</c>, so the
/// supervisor interaction cannot be mocked. Per the design seam decision:
///   - the pure projection (<c>WatchConfig</c> → core <see cref="WatchOptions"/>) is
///     extracted into the static <see cref="WatchHostedService.ProjectOptions"/> helper
///     and unit-tested directly here;
///   - the no-valid-inputs clean-return path is exercised with a REAL
///     <see cref="WatchSupervisor"/> built around a throwing task factory (never invoked
///     because there are no valid inputs);
///   - full supervisor interaction (RunAsync forwarding, re-throw, prompt cancellation)
///     is covered by the service-mode E2E tests.
/// </summary>
public class WatchHostedServiceTests
{
    private static WatchSupervisor NewSupervisor() => new(
        _ => throw new InvalidOperationException("task factory must not be called in these tests"),
        TimeProvider.System,
        Log.Logger);

    [Fact]
    public void ProjectOptions_MapsEveryField()
    {
        var watch = new WatchConfig
        {
            Inputs = new[] { "/in/one", "/in/two" },
            Output = "/out",
            Integrity = IntegrityChoice.L3,
            Concurrency = 7,
            ApiConcurrency = 3,
            PollIntervalSeconds = 11,
            StabilityChecks = 5,
            MinFileSize = 4096,
            WriteTags = false,
            ApiRateLimit = 9.0,
        };

        var options = WatchHostedService.ProjectOptions(watch);

        options.InputDirs.Should().Equal("/in/one", "/in/two");
        options.OutputDir.Should().Be("/out");
        options.Integrity.Should().Be(IntegrityLevel.L3);
        options.Concurrency.Should().Be(7);
        options.PollInterval.Should().Be(TimeSpan.FromSeconds(11));
        options.StabilityChecks.Should().Be(5);
        options.MinFileSizeBytes.Should().Be(4096);
        options.WriteTags.Should().BeFalse();
        options.ApiRateLimit.Should().Be(9);
    }

    [Theory]
    [InlineData(IntegrityChoice.Off, IntegrityLevel.Off)]
    [InlineData(IntegrityChoice.L1, IntegrityLevel.L1)]
    [InlineData(IntegrityChoice.L3, IntegrityLevel.L3)]
    [InlineData(IntegrityChoice.Auto, IntegrityLevel.Auto)]
    public void ProjectOptions_MapsIntegrityChoice(IntegrityChoice choice, IntegrityLevel expected)
    {
        var watch = new WatchConfig { Output = "/out", Integrity = choice };

        WatchHostedService.ProjectOptions(watch).Integrity.Should().Be(expected);
    }

    [Fact]
    public void ProjectOptions_NullApiRateLimit_UsesDefault()
    {
        var watch = new WatchConfig { Output = "/out", ApiRateLimit = null };

        WatchHostedService.ProjectOptions(watch).ApiRateLimit.Should().Be(MetadataDefaults.ApiRateLimit);
    }

    [Fact]
    public void Constructor_RejectsNullArguments()
    {
        var supervisor = NewSupervisor();
        var config = new RootConfig();
        var log = NullLogger<WatchHostedService>.Instance;

        Action nullSupervisor = () => new WatchHostedService(null!, config, log);
        Action nullConfig = () => new WatchHostedService(supervisor, null!, log);
        Action nullLog = () => new WatchHostedService(supervisor, config, null!);

        nullSupervisor.Should().Throw<ArgumentNullException>();
        nullConfig.Should().Throw<ArgumentNullException>();
        nullLog.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_NoValidInputs_ReturnsCleanly()
    {
        // No inputs configured → ParseAndValidateInputs yields zero valid → clean no-op.
        var config = new RootConfig { Watch = new WatchConfig { Inputs = Array.Empty<string>(), Output = "/out" } };
        var service = new WatchHostedService(NewSupervisor(), config, NullLogger<WatchHostedService>.Instance);

        var run = service.StartAsync(CancellationToken.None);

        // BackgroundService.StartAsync returns once ExecuteAsync yields or completes; the
        // no-valid-inputs path completes synchronously without invoking the task factory.
        await run;
        await service.StopAsync(CancellationToken.None);
    }
}
