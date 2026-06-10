using System;
using System.Net.Http;
using Autofac;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;

namespace FreeBird.Core.DependencyInjection;

/// <summary>
/// Autofac module that registers all FreeBird.Core types implementing IDependency
/// as their implemented interfaces with InstancePerLifetimeScope.
///
/// Also registers the system <see cref="TimeProvider"/> singleton, used by watch-mode
/// components (e.g. <c>WatchOrchestrator</c>) so they can be tested with FakeTimeProvider.
///
/// To register Core services in a host (CLI, GUI, tests), add:
///   builder.RegisterModule&lt;CoreModule&gt;();
/// </summary>
public sealed class CoreModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        // Assembly-scan all concrete IDependency implementations.
        //
        // EXCLUSION (T13): Both IFileNamer implementations are excluded from the scan
        // and registered explicitly below. Rationale:
        //   - StemBasedFileNamer (v1/v2 IFileNamer) stays in the codebase for direct
        //     instantiation (its GetStem helper is used by quarantine/skip deciders,
        //     and tests construct it via `new StemBasedFileNamer()`), but is NOT
        //     DI-bound in v3.
        //   - MetadataAwareFileNamer (v3 IFileNamer) is registered explicitly so the
        //     binding intent is obvious and so we don't get a duplicate registration
        //     from the scan layered under the explicit one.
        builder.RegisterAssemblyTypes(typeof(CoreModule).Assembly)
               .Where(t => typeof(IDependency).IsAssignableFrom(t)
                          && !t.IsAbstract
                          && !t.IsInterface
                          && t.IsClass
                          && t != typeof(FreeBird.Core.Decoding.StemBasedFileNamer)
                          && t != typeof(FreeBird.Core.Naming.MetadataAwareFileNamer))
               .AsImplementedInterfaces()
               .InstancePerLifetimeScope();

        // TimeProvider is the .NET 8+ abstraction over the system clock + timer scheduling.
        // Watch-mode components inject it so unit tests can substitute FakeTimeProvider.
        builder.RegisterInstance(TimeProvider.System)
               .As<TimeProvider>()
               .SingleInstance();

        // SizeStabilityCompletionDetector is stateful (per-file observation history).
        // It MUST be SingleInstance so state persists across watch cycles and across
        // any child lifetime scopes a host might open. Last-registration-wins
        // overrides the bulk InstancePerLifetimeScope above for this specific service.
        builder.RegisterType<FreeBird.Core.Watch.SizeStabilityCompletionDetector>()
               .As<FreeBird.Core.Abstractions.ICompletionDetector>()
               .SingleInstance();

        // NamingTemplateRenderer is pure / stateless. SingleInstance is a small
        // allocation optimization — no per-scope copies of an immutable function object.
        // Last-registration-wins overrides the bulk InstancePerLifetimeScope above.
        builder.RegisterType<FreeBird.Core.Naming.NamingTemplateRenderer>()
               .As<FreeBird.Core.Abstractions.INamingTemplateRenderer>()
               .SingleInstance();

        // DefaultMetadataOptions (T13): spec-default IMetadataOptions so the container
        // graph can resolve at startup. Per-run options (ScanOptions/WatchOptions) flow
        // through orchestrator method parameters (FileProcessor.ProcessAsync in T14),
        // NOT the container. This default exists only so MetadataAwareFileNamer's
        // ctor dependency on IMetadataOptions is satisfied at compose time.
        builder.RegisterType<DefaultMetadataOptions>()
               .As<IMetadataOptions>()
               .SingleInstance();

        // MetadataAwareFileNamer (T13): v3 IFileNamer. Composes INamingTemplateRenderer
        // + FilenameSanitizer + extension. Explicit registration with last-wins
        // semantics overrides any IFileNamer the assembly-scan picked up.
        // SingleInstance: stateless / pure, matches surrounding renderer pattern.
        builder.RegisterType<FreeBird.Core.Naming.MetadataAwareFileNamer>()
               .As<IFileNamer>()
               .SingleInstance();

        // HttpClient — Autofac-native registration (Amendment 2).
        // We deliberately do NOT use Microsoft.Extensions.Http / IHttpClientFactory;
        // FreeBird has exactly one HTTP consumer (NetEaseApiClient) so a single
        // long-lived HttpClient with a pooled SocketsHttpHandler is sufficient and
        // avoids dragging the MS.Extensions.* DI stack into a pure Autofac app.
        //
        // - PooledConnectionLifetime = 15min: refreshes DNS / honours connection
        //   recycling on long-running watch sessions.
        // - Timeout = 30s: an outer ceiling. NetEaseApiClient enforces the user's
        //   --api-timeout per-call via CancellationToken (always shorter than this).
        // - User-Agent: a real Safari/macOS string. NetEase's WAF blocks the default
        //   .NET UA ("FreeBird/1.0") with 403, so we masquerade as a browser per
        //   spec §5 Headers.
        builder.Register(c =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15)
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15");
            return client;
        }).As<HttpClient>().SingleInstance();
    }
}
