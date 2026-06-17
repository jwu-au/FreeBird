using System;
using System.Linq;
using Autofac;
using FluentAssertions;
using FreeBird.Cli.DependencyInjection;
using FreeBird.Cli.Service;
using FreeBird.Core.DependencyInjection;
using Serilog;
using Xunit;

namespace FreeBird.Cli.Tests.DependencyInjection;

/// <summary>
/// T14 — registration tests for <see cref="CliServiceModule"/>.
///
/// Pins down:
///   (1) the module selects the correct service impls by <c>OperatingSystem.IsWindows()</c>;
///   (2) <see cref="IEventLogWriter"/> is NOT a singleton (different lifetime scopes →
///       different instances);
///   (3) the Cli-only service types never leaked into the Core assembly (B-5/B-6
///       invariant: <see cref="CoreModule"/> assembly-scans Core only).
/// </summary>
public class CliServiceModuleTests
{
    private static IContainer BuildContainer()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        builder.RegisterModule<CliServiceModule>();
        builder.RegisterInstance(Log.Logger).As<ILogger>();
        return builder.Build();
    }

    [Fact]
    public void ServiceImpls_AreSelectedByOperatingSystem()
    {
        using var container = BuildContainer();
        using var scope = container.BeginLifetimeScope();

        var controller = scope.Resolve<IServiceController>();
        var elevation = scope.Resolve<IElevationChecker>();
        var eventLog = scope.Resolve<IEventLogWriter>();
        var logPaths = scope.Resolve<ILogPathResolver>();

        controller.Should().NotBeNull();
        elevation.Should().NotBeNull();
        eventLog.Should().NotBeNull();
        logPaths.Should().BeOfType<LogPathResolver>();

        if (OperatingSystem.IsWindows())
        {
            // WindowsServiceController / WindowsEventLogWriter sources are excluded from the
            // non-Windows compile set (FreeBird.Cli.csproj), so assert by type name to keep this
            // test file building cross-OS. WindowsElevationChecker IS compiled everywhere.
            controller.GetType().Name.Should().Be("WindowsServiceController");
            elevation.Should().BeOfType<WindowsElevationChecker>();
            eventLog.GetType().Name.Should().Be("WindowsEventLogWriter");
        }
        else
        {
            controller.Should().BeOfType<NotSupportedServiceController>();
            elevation.Should().BeOfType<NonWindowsElevationChecker>();
            eventLog.Should().BeOfType<NotSupportedEventLogWriter>();
        }
    }

    [Fact]
    public void EventLogWriter_IsNotSingleInstance_AcrossLifetimeScopes()
    {
        using var container = BuildContainer();

        IEventLogWriter first;
        IEventLogWriter second;
        using (var scopeA = container.BeginLifetimeScope())
        {
            first = scopeA.Resolve<IEventLogWriter>();
        }

        using (var scopeB = container.BeginLifetimeScope())
        {
            second = scopeB.Resolve<IEventLogWriter>();
        }

        ReferenceEquals(first, second).Should().BeFalse(
            "IEventLogWriter is a stateless per-lifetime-scope utility, not SingleInstance");
    }

    [Fact]
    public void CoreAssembly_DoesNotContainCliServiceModule()
    {
        var coreTypeNames = typeof(CoreModule).Assembly
            .GetTypes()
            .Select(t => t.Name)
            .ToArray();

        coreTypeNames.Should().NotContain("CliServiceModule");
        coreTypeNames.Should().NotContain("WindowsServiceModule");
    }
}
