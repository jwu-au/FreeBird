using System;
using Autofac;
using FreeBird.Core.Abstractions;

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
        builder.RegisterAssemblyTypes(typeof(CoreModule).Assembly)
               .Where(t => typeof(IDependency).IsAssignableFrom(t)
                          && !t.IsAbstract
                          && !t.IsInterface
                          && t.IsClass)
               .AsImplementedInterfaces()
               .InstancePerLifetimeScope();

        // TimeProvider is the .NET 8+ abstraction over the system clock + timer scheduling.
        // Watch-mode components inject it so unit tests can substitute FakeTimeProvider.
        builder.RegisterInstance(TimeProvider.System)
               .As<TimeProvider>()
               .SingleInstance();
    }
}
