using Autofac;
using FreeBird.Cli.DependencyInjection;
using FreeBird.Core.DependencyInjection;
using FreeBird.Core.Service;

namespace FreeBird.Cli.Service;

/// <summary>
/// T13 — builds the Autofac container for service-mode (<c>fb service</c> / Windows SCM host)
/// from a parsed <see cref="RootConfig"/>.
///
/// This is the service-mode counterpart of <c>WatchRunner.BuildContainer</c> and MUST stay in
/// sync with it. Both register <c>CoreModule</c> + <c>CliServiceModule</c>, pin the Serilog
/// logger as a singleton, and override <c>CoreModule</c>'s default
/// <see cref="FreeBird.Core.Provisioning.FlacResolverOptions"/> with the caller-supplied FLAC
/// knobs (CLI flags / env vars on the runner side; <c>watch.flac.*</c> config here).
///
/// IMPORTANT: any future FLAC / CLI knob added to one of these builders must be added to the
/// other, or service mode and interactive watch mode will silently diverge.
///
/// Unlike <c>WatchRunner.BuildContainer</c>, this builder deliberately contains NO test-hook
/// blocks (no <c>OrchestratorFactoryOverride</c> / <c>AdditionalContainerSetup</c>): service mode
/// is exercised end-to-end, not via static seams.
/// </summary>
public static class ServiceHostBuilder
{
    public static IContainer BuildContainer(Serilog.ILogger logger, RootConfig config)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(config);

        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        builder.RegisterModule<CliServiceModule>();
        builder.RegisterInstance(logger).As<Serilog.ILogger>().SingleInstance();

        // Override CoreModule's default FlacResolverOptions with the watch.flac.* config knobs.
        // Autofac's last-registration-wins makes this resolve in preference to the in-module
        // default. AppBaseDirectory is still pulled from IAppBaseDirectoryProvider so
        // AppContext.BaseDirectory is honored (parity with WatchRunner.BuildContainer).
        builder.Register(c => new FreeBird.Core.Provisioning.FlacResolverOptions
        {
            AppBaseDirectory = c.Resolve<FreeBird.Core.Provisioning.IAppBaseDirectoryProvider>().GetBaseDirectory(),
            FlacBinOverride = config.Watch.Flac?.Bin,
            MetaflacBinOverride = null,
            AutoInstallUrl = config.Watch.Flac?.Url,
            DisableAutoInstall = config.Watch.Flac?.DisableAutoInstall ?? false,
        }).AsSelf().SingleInstance();

        return builder.Build();
    }

    /// <summary>
    /// T22 — config-less overload for the <c>fb service</c> leaves that run BEFORE (or entirely
    /// without) a parsed config: <c>start</c> / <c>stop</c> / <c>restart</c> / <c>status</c> /
    /// <c>uninstall</c>. These only need the SCM-control trio
    /// (<c>IServiceController</c> / <c>IElevationChecker</c> / <c>IEventLogWriter</c>) plus the
    /// pinned Serilog logger; they never resolve the FLAC pipeline, so a default
    /// <see cref="FreeBird.Core.Provisioning.FlacResolverOptions"/> (from <c>CoreModule</c>) is
    /// sufficient and no <c>watch.flac.*</c> override is applied.
    /// </summary>
    public static IContainer BuildContainer(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        builder.RegisterModule<CliServiceModule>();
        builder.RegisterInstance(logger).As<Serilog.ILogger>().SingleInstance();
        return builder.Build();
    }
}
