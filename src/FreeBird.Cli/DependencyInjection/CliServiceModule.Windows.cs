using System.Runtime.Versioning;
using Autofac;
using FreeBird.Cli.Service;

namespace FreeBird.Cli.DependencyInjection;

/// <summary>
/// T14 — Windows-only half of <see cref="CliServiceModule"/>. This file is excluded from the
/// macOS / Linux compile set (see FreeBird.Cli.csproj) because it references
/// <see cref="WindowsServiceController"/> / <see cref="WindowsEventLogWriter"/>, whose sources and
/// Windows-only NuGets are not present off Windows. On those hosts the matching
/// <c>partial void RegisterWindows</c> declaration in the base file has no body, so the call site
/// is elided and the module still builds.
/// </summary>
public sealed partial class CliServiceModule
{
    [SupportedOSPlatform("windows")]
    partial void RegisterWindows(ContainerBuilder builder)
    {
        builder.RegisterType<WindowsServiceController>()
               .As<IServiceController>()
               .InstancePerLifetimeScope();
        builder.RegisterType<WindowsElevationChecker>()
               .As<IElevationChecker>()
               .InstancePerLifetimeScope();
        builder.RegisterType<WindowsEventLogWriter>()
               .As<IEventLogWriter>()
               .InstancePerLifetimeScope();
    }
}
