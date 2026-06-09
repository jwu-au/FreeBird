using System;
using Autofac;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.DependencyInjection;
using FreeBird.Core.Sidecar;
using FreeBird.Core.Watch;
using Serilog;

namespace FreeBird.Core.Tests.DependencyInjection;

/// <summary>
/// Integration tests verifying that the four v2 watch-mode components
/// (T04 TextSidecarReader, T06 SizeStabilityCompletionDetector,
/// T07 FilesystemSkipDecider, T08 WatchOrchestrator) resolve through the
/// existing <see cref="CoreModule"/> IDependency assembly scan — no
/// dedicated WatchModule is required because each interface extends
/// <c>IDependency</c>.
/// </summary>
public class WatchComponentsResolutionTests
{
    private static IContainer BuildContainer()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        var logger = new LoggerConfiguration().CreateLogger();
        builder.RegisterInstance<ILogger>(logger).SingleInstance();
        return builder.Build();
    }

    [Fact]
    public void CoreModule_Resolves_ISidecarReader_AsTextSidecarReader()
    {
        using var container = BuildContainer();
        var reader = container.Resolve<ISidecarReader>();
        reader.Should().BeOfType<TextSidecarReader>();
    }

    [Fact]
    public void CoreModule_Resolves_ICompletionDetector_AsSizeStabilityCompletionDetector()
    {
        using var container = BuildContainer();
        var detector = container.Resolve<ICompletionDetector>();
        detector.Should().BeOfType<SizeStabilityCompletionDetector>();
    }

    [Fact]
    public void CoreModule_Resolves_ISkipDecider_AsFilesystemSkipDecider()
    {
        using var container = BuildContainer();
        var decider = container.Resolve<ISkipDecider>();
        decider.Should().BeOfType<FilesystemSkipDecider>();
    }

    [Fact]
    public void CoreModule_Resolves_IWatchOrchestrator_AsWatchOrchestrator()
    {
        using var container = BuildContainer();
        var orchestrator = container.Resolve<IWatchOrchestrator>();
        orchestrator.Should().BeOfType<WatchOrchestrator>();
    }

    [Fact]
    public void CoreModule_Resolves_TimeProvider_AsSystem()
    {
        using var container = BuildContainer();
        var tp = container.Resolve<TimeProvider>();
        tp.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void CoreModule_ResolvingIWatchOrchestrator_ResolvesFullDependencyGraph()
    {
        // Verifies that all 6 dependencies of WatchOrchestrator
        // (IScanOrchestrator, ICompletionDetector, ISkipDecider, IFileProcessor,
        // TimeProvider, ILogger) are registered — if any one were missing,
        // Autofac would throw during resolution.
        using var container = BuildContainer();
        var act = () => container.Resolve<IWatchOrchestrator>();
        act.Should().NotThrow();
    }

    [Fact]
    public void CoreModule_ICompletionDetector_IsSingleInstance_AcrossScopes()
    {
        // SizeStabilityCompletionDetector holds per-file observation history in a
        // ConcurrentDictionary. State MUST persist across watch cycles and across
        // any child lifetime scopes a host opens — otherwise no file is ever
        // judged stable and the orchestrator processes nothing.
        using var container = BuildContainer();
        using var scopeA = container.BeginLifetimeScope();
        using var scopeB = container.BeginLifetimeScope();

        var fromA = scopeA.Resolve<ICompletionDetector>();
        var fromB = scopeB.Resolve<ICompletionDetector>();
        var fromRoot = container.Resolve<ICompletionDetector>();

        fromA.Should().BeSameAs(fromB, because: "detector state must persist across all lifetime scopes");
        fromA.Should().BeSameAs(fromRoot, because: "child scope and root scope must share the singleton");
    }
}
