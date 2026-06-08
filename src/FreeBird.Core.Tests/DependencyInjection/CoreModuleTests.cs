using System;
using System.Linq;
using Autofac;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.DependencyInjection;
using Serilog;

namespace FreeBird.Core.Tests.DependencyInjection;

public class CoreModuleTests
{
    private static IContainer BuildContainer()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        // Host must register ILogger; tests use a silent one
        var logger = new LoggerConfiguration().CreateLogger();
        builder.RegisterInstance<ILogger>(logger).SingleInstance();
        return builder.Build();
    }

    [Theory]
    [InlineData(typeof(IXorDecoder))]
    [InlineData(typeof(IFormatSniffer))]
    [InlineData(typeof(INamingStrategy))]
    [InlineData(typeof(IProcessRunner))]
    [InlineData(typeof(IAtomicFileWriter))]
    [InlineData(typeof(IL1IntegrityChecker))]
    [InlineData(typeof(IL3IntegrityChecker))]
    [InlineData(typeof(IFlacProbe))]
    [InlineData(typeof(ICompositeIntegrityChecker))]
    [InlineData(typeof(IFileProcessor))]
    [InlineData(typeof(IScanOrchestrator))]
    public void CoreModule_ResolvesAllPublicInterfaces(Type serviceType)
    {
        using var container = BuildContainer();
        var instance = container.Resolve(serviceType);
        instance.Should().NotBeNull();
        serviceType.IsInstanceOfType(instance).Should().BeTrue();
    }

    [Fact]
    public void CoreModule_FileProcessor_ResolvesFullGraph()
    {
        using var container = BuildContainer();
        var fp = container.Resolve<IFileProcessor>();
        fp.Should().NotBeNull();
        fp.GetType().Name.Should().Be("FileProcessor");
    }

    [Fact]
    public void CoreModule_ScanOrchestrator_ResolvesWithLogger()
    {
        using var container = BuildContainer();
        var orch = container.Resolve<IScanOrchestrator>();
        orch.Should().NotBeNull();
        orch.GetType().Name.Should().Be("ScanOrchestrator");
    }

    [Fact]
    public void CoreModule_InstancePerLifetimeScope_SameInstanceWithinScope()
    {
        using var container = BuildContainer();
        using var scope = container.BeginLifetimeScope();
        var a = scope.Resolve<IXorDecoder>();
        var b = scope.Resolve<IXorDecoder>();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void CoreModule_DifferentScopes_DifferentInstances()
    {
        using var container = BuildContainer();
        using var scope1 = container.BeginLifetimeScope();
        using var scope2 = container.BeginLifetimeScope();
        var a = scope1.Resolve<IXorDecoder>();
        var b = scope2.Resolve<IXorDecoder>();
        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void CoreModule_CompositeIntegrityChecker_AlsoResolvesAsBaseInterface()
    {
        using var container = BuildContainer();
        // CompositeIntegrityChecker implements both ICompositeIntegrityChecker and IIntegrityChecker
        var asComposite = container.Resolve<ICompositeIntegrityChecker>();
        var asBase = container.Resolve<IIntegrityChecker>();
        // Both should be CompositeIntegrityChecker (the only one implementing ICompositeIntegrityChecker)
        asComposite.GetType().Name.Should().Be("CompositeIntegrityChecker");
        // Note: IIntegrityChecker is also implemented by L1/L3 checkers. Resolving the base might pick any.
        // We just verify it resolves without error.
        asBase.Should().NotBeNull();
    }

    [Fact]
    public void CoreModule_RegistersAllConcreteIDependencies()
    {
        var assembly = typeof(CoreModule).Assembly;
        var expectedCount = assembly.GetTypes()
            .Count(t => typeof(IDependency).IsAssignableFrom(t)
                       && !t.IsAbstract && !t.IsInterface && t.IsClass);

        expectedCount.Should().BeGreaterThan(8, "we have at least 9 IDependency implementations in v1");

        using var container = BuildContainer();
        // Try to resolve each concrete type via its primary interface
        foreach (var concreteType in assembly.GetTypes().Where(t =>
            typeof(IDependency).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface && t.IsClass))
        {
            var primaryInterface = concreteType.GetInterfaces()
                .FirstOrDefault(i => i != typeof(IDependency)
                                    && typeof(IDependency).IsAssignableFrom(i));
            if (primaryInterface is null) { continue; }
            container.IsRegistered(primaryInterface).Should().BeTrue(
                $"interface {primaryInterface.Name} should be registered for concrete {concreteType.Name}");
        }
    }
}
