using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using Autofac;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.DependencyInjection;
using FreeBird.Core.Naming;
using FreeBird.Core.Tagging;
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
    [InlineData(typeof(IFileNamer))]
    [InlineData(typeof(IProcessRunner))]
    [InlineData(typeof(IAtomicFileWriter))]
    [InlineData(typeof(IL1IntegrityChecker))]
    [InlineData(typeof(IL3IntegrityChecker))]
    [InlineData(typeof(IFlacProbe))]
    [InlineData(typeof(ICompositeIntegrityChecker))]
    [InlineData(typeof(IFileProcessor))]
    [InlineData(typeof(IScanOrchestrator))]
    [InlineData(typeof(INamingTemplateRenderer))]
    [InlineData(typeof(INetEaseApiClient))]
    [InlineData(typeof(IMetadataResolver))]
    [InlineData(typeof(IMetadataOptions))]
    [InlineData(typeof(ITagWriter))]
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
    public void CoreModule_NamingTemplateRenderer_IsSingleton_AcrossScopes()
    {
        // NamingTemplateRenderer is registered SingleInstance (pure / stateless).
        // The same instance should be returned across different lifetime scopes.
        using var container = BuildContainer();
        using var scope1 = container.BeginLifetimeScope();
        using var scope2 = container.BeginLifetimeScope();
        var a = scope1.Resolve<INamingTemplateRenderer>();
        var b = scope2.Resolve<INamingTemplateRenderer>();
        a.Should().BeSameAs(b);
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

    // ------------------------------------------------------------------
    // T09 — Autofac-native HttpClient SingleInstance registration
    // ------------------------------------------------------------------

    [Fact]
    public void Container_Resolves_HttpClient()
    {
        using var container = BuildContainer();
        var client = container.Resolve<HttpClient>();
        client.Should().NotBeNull();
    }

    [Fact]
    public void HttpClient_IsSingleInstance()
    {
        using var container = BuildContainer();
        var a = container.Resolve<HttpClient>();
        var b = container.Resolve<HttpClient>();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void HttpClient_HasUserAgent()
    {
        using var container = BuildContainer();
        var client = container.Resolve<HttpClient>();
        var ua = client.DefaultRequestHeaders.UserAgent.ToString();
        ua.Should().Contain("Mozilla/5.0");
        ua.Should().Contain("Safari/605.1.15");
    }

    [Fact]
    public void HttpClient_HasCeilingTimeout()
    {
        using var container = BuildContainer();
        var client = container.Resolve<HttpClient>();
        client.Timeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Csproj_DoesNotContain_MicrosoftExtensionsHttp()
    {
        // Amendment 2 guard: FreeBird must not depend on Microsoft.Extensions.Http
        // or Autofac.Extensions.DependencyInjection — HttpClient is registered
        // Autofac-natively in CoreModule.
        var csprojPath = FindCoreCsproj();
        var text = File.ReadAllText(csprojPath);
        text.Should().NotContain("Microsoft.Extensions.Http");
        text.Should().NotContain("Autofac.Extensions.DependencyInjection");
    }

    // ------------------------------------------------------------------
    // T13 — DI swap: IFileNamer → MetadataAwareFileNamer
    // ------------------------------------------------------------------

    [Fact]
    public void IFileNamer_ResolvesAs_MetadataAwareFileNamer()
    {
        using var container = BuildContainer();
        var namer = container.Resolve<IFileNamer>();
        namer.GetType().Should().Be(typeof(MetadataAwareFileNamer));
    }

    [Fact]
    public void StemBasedFileNamer_NoLongerBound()
    {
        // StemBasedFileNamer remains in the codebase for direct instantiation
        // (v1/v2-style tests `new StemBasedFileNamer()`), but is excluded from
        // the assembly-scan in CoreModule so it is no longer DI-resolvable —
        // neither as itself nor as the resolved IFileNamer.
        using var container = BuildContainer();
        container.IsRegistered<StemBasedFileNamer>().Should().BeFalse();
        var namer = container.Resolve<IFileNamer>();
        namer.Should().NotBeOfType<StemBasedFileNamer>();

        // Also assert the implementation list for IFileNamer contains exactly one
        // type and it isn't StemBasedFileNamer — defense against future scan changes.
        var allFileNamers = container.Resolve<System.Collections.Generic.IEnumerable<IFileNamer>>().ToList();
        allFileNamers.Should().ContainSingle();
        allFileNamers.Should().NotContain(n => n.GetType() == typeof(StemBasedFileNamer));
    }

    [Fact]
    public void IMetadataOptions_Resolves_AsDefault()
    {
        // CoreModule registers a DefaultMetadataOptions so MetadataAwareFileNamer's
        // ctor graph can resolve at container build time. Per-run options flow
        // via method parameters (FileProcessor.ProcessAsync), not the container.
        using var container = BuildContainer();
        var options = container.Resolve<IMetadataOptions>();
        options.Should().NotBeNull();
        options.NamingTemplate.Should().Be("{artist} - {title}");
        options.Offline.Should().BeFalse();
        options.ApiTimeoutSeconds.Should().Be(10);
        options.ApiRateLimit.Should().Be(0);
        options.WriteTags.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // T17 — ITagWriter binding + tag-writer wiring
    // ------------------------------------------------------------------

    [Fact]
    public void ITagWriter_ResolvesAs_CompositeTagWriter()
    {
        using var container = BuildContainer();
        var writer = container.Resolve<ITagWriter>();
        writer.GetType().Should().Be(typeof(CompositeTagWriter));
    }

    [Fact]
    public void ITagWriter_IsSingleInstance_AcrossScopes()
    {
        using var container = BuildContainer();
        using var scope1 = container.BeginLifetimeScope();
        using var scope2 = container.BeginLifetimeScope();
        var a = scope1.Resolve<ITagWriter>();
        var b = scope2.Resolve<ITagWriter>();
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void TagWriter_Backends_ResolveByInterface()
    {
        using var container = BuildContainer();
        container.Resolve<IFlacTagWriter>().GetType().Should().Be(typeof(FlacTagWriter));
        container.Resolve<IMp3TagWriter>().GetType().Should().Be(typeof(Mp3TagWriter));
        container.Resolve<IM4aTagWriter>().GetType().Should().Be(typeof(M4aTagWriter));
    }

    [Fact]
    public void ResolutionMarkerSerializer_Resolves_AsSingleInstance()
    {
        using var container = BuildContainer();
        var a = container.Resolve<FreeBird.Core.Processing.ResolutionMarkerSerializer>();
        var b = container.Resolve<FreeBird.Core.Processing.ResolutionMarkerSerializer>();
        Assert.NotNull(a);
        Assert.Same(a, b);
    }

    private static string FindCoreCsproj()
    {
        // Walk up from the test assembly's directory to find the repo root,
        // then locate src/FreeBird.Core/FreeBird.Core.csproj.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FreeBird.Core", "FreeBird.Core.csproj");
            if (File.Exists(candidate)) { return candidate; }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate FreeBird.Core.csproj walking up from " + AppContext.BaseDirectory);
    }
}
