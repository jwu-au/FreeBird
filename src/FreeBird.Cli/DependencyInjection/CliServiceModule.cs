using System;
using Autofac;
using FreeBird.Cli.Service;

namespace FreeBird.Cli.DependencyInjection;

/// <summary>
/// T14 — registers the CLI-side Windows-Service support types.
///
/// These interfaces (<see cref="IServiceController"/>, <see cref="IElevationChecker"/>,
/// <see cref="IEventLogWriter"/>) all extend <c>IDependency</c> but are intentionally NOT
/// auto-discovered by <c>CoreModule</c>'s reflection scan (that scan covers the Core assembly
/// only). This module picks the correct implementation per host OS:
///   - Windows → the real SCM / Win32 / Event Log impls;
///   - everything else → the <c>NotSupported*</c> / <c>NonWindows*</c> fallbacks so that
///     install/uninstall guards trip cleanly with a clear message instead of throwing on resolve.
///
/// All registrations are <c>InstancePerLifetimeScope</c> (the default): these types are stateless
/// utilities, so they deliberately do NOT get a <c>SingleInstance</c> carve-out.
///
/// Compile-split: the Windows-only registrations reference
/// <c>WindowsServiceController</c> / <c>WindowsEventLogWriter</c>, whose source files (and their
/// Windows-only NuGets) are excluded from the macOS / Linux compile set (see
/// FreeBird.Cli.csproj). They therefore live in the sibling partial
/// <c>CliServiceModule.Windows.cs</c>, which is excluded on non-Windows too. The
/// <see cref="RegisterWindows"/> partial method is a no-op when that file is absent, so this base
/// file builds on every OS while still selecting the real impls on Windows.
/// </summary>
public sealed partial class CliServiceModule : Autofac.Module
{
    protected override void Load(ContainerBuilder builder)
    {
        if (OperatingSystem.IsWindows())
        {
            RegisterWindows(builder);
        }
        else
        {
            builder.RegisterType<NotSupportedServiceController>()
                   .As<IServiceController>()
                   .InstancePerLifetimeScope();
            builder.RegisterType<NonWindowsElevationChecker>()
                   .As<IElevationChecker>()
                   .InstancePerLifetimeScope();
            builder.RegisterType<NotSupportedEventLogWriter>()
                   .As<IEventLogWriter>()
                   .InstancePerLifetimeScope();
        }

        // OS-agnostic: resolves the log file path from an injected IFileSystem.
        builder.RegisterType<LogPathResolver>()
               .As<ILogPathResolver>()
               .InstancePerLifetimeScope();
    }

    /// <summary>
    /// Registers the real Windows SCM / Win32 / Event Log service impls. Implemented in
    /// <c>CliServiceModule.Windows.cs</c>, which is compiled on Windows only; on macOS / Linux
    /// this partial method has no body and the call is elided by the compiler. Guarded by
    /// <c>OperatingSystem.IsWindows()</c> at the single call site.
    /// </summary>
    partial void RegisterWindows(ContainerBuilder builder);
}
