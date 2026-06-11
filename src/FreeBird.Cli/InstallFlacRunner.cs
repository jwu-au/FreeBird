using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FreeBird.Core.DependencyInjection;
using FreeBird.Core.Provisioning;
using Serilog;
using Serilog.Events;

namespace FreeBird.Cli;

/// <summary>
/// Pure install-flac execution: builds container, resolves
/// <see cref="IFlacAutoInstaller"/>, invokes it once, maps the result to an exit code.
/// Separated from <see cref="InstallFlacCommand"/> so it can be unit-tested by
/// substituting the container via <see cref="ContainerOverride"/>.
/// </summary>
public static class InstallFlacRunner
{
    /// <summary>
    /// Default pinned URL — kept in sync with
    /// <c>WindowsFlacAutoInstaller.ExpectedSha256</c> (Xiph OSUOSL flac 1.5.0 Windows ZIP).
    /// </summary>
    public const string DefaultUrl = "https://ftp.osuosl.org/pub/xiph/releases/flac/flac-1.5.0-win.zip";

    /// <summary>
    /// Test-only hook (mirrors <c>ScanRunner.AdditionalContainerSetup</c> and
    /// <c>WatchRunner.OrchestratorFactoryOverride</c>): if non-null, returns the
    /// Autofac <see cref="IContainer"/> to use instead of the default
    /// <see cref="BuildContainer"/>. Tests use this to inject a mock
    /// <see cref="IFlacAutoInstaller"/>; reset to null between tests via Dispose.
    /// </summary>
    public static Func<ILogger, IContainer>? ContainerOverride { get; set; }

    public static async Task<int> RunAsync(
        string? targetDir,
        string? overrideUrl,
        bool verbose,
        bool quiet,
        CancellationToken ct)
    {
        if (verbose && quiet)
        {
            await Console.Error.WriteLineAsync("--verbose and --quiet are mutually exclusive.").ConfigureAwait(false);
            return InstallFlacCommand.ExitBadArgs;
        }

        var effectiveTarget = string.IsNullOrWhiteSpace(targetDir)
            ? AppContext.BaseDirectory
            : targetDir;
        var effectiveUrl = string.IsNullOrWhiteSpace(overrideUrl)
            ? DefaultUrl
            : overrideUrl;

        if (!Uri.TryCreate(effectiveUrl, UriKind.Absolute, out _))
        {
            await Console.Error.WriteLineAsync($"Invalid URL: {effectiveUrl}").ConfigureAwait(false);
            return InstallFlacCommand.ExitBadArgs;
        }

        var logger = BuildLogger(verbose, quiet);

        try
        {
            var container = (ContainerOverride is not null)
                ? ContainerOverride(logger)
                : BuildContainer(logger);
            await using (container)
            await using (var scope = container.BeginLifetimeScope())
            {
                var installer = scope.Resolve<IFlacAutoInstaller>();
                logger.Information(
                    "Installing flac binaries to {Dir} from {Url}",
                    effectiveTarget,
                    effectiveUrl);

                var result = await installer
                    .InstallAsync(effectiveTarget, effectiveUrl, ct)
                    .ConfigureAwait(false);

                return result switch
                {
                    FlacInstallResult.Installed installed => HandleInstalled(installed, logger),
                    FlacInstallResult.NotSupported => HandleNotSupported(logger),
                    FlacInstallResult.Failed failed => HandleFailed(failed, logger),
                    FlacInstallResult.Disabled => HandleDisabled(logger),
                    _ => HandleUnknown(result, logger),
                };
            }
        }
        catch (OperationCanceledException)
        {
            logger.Information("install-flac cancelled");
            return InstallFlacCommand.ExitFailed;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "install-flac failed unexpectedly");
            return InstallFlacCommand.ExitFailed;
        }
    }

    private static IContainer BuildContainer(ILogger logger)
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        builder.RegisterInstance(logger).As<ILogger>().SingleInstance();
        return builder.Build();
    }

    private static ILogger BuildLogger(bool verbose, bool quiet)
    {
        var level = quiet ? LogEventLevel.Warning
                  : verbose ? LogEventLevel.Debug
                  : LogEventLevel.Information;
        return new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static int HandleInstalled(FlacInstallResult.Installed installed, ILogger logger)
    {
        var dir = Path.GetDirectoryName(installed.FlacPath);
        logger.Information(
            "flac installed successfully (flac: {Flac}, metaflac: {Metaflac}, dir: {Dir})",
            installed.FlacPath,
            installed.MetaflacPath,
            dir);
        return InstallFlacCommand.ExitOk;
    }

    private static int HandleNotSupported(ILogger logger)
    {
        logger.Information(
            "Auto-install is not supported on this platform. Please install flac via your package manager: "
            + "macOS -> `brew install flac`, Debian/Ubuntu -> `sudo apt install flac`, "
            + "others see https://xiph.org/flac/");
        return InstallFlacCommand.ExitOk;
    }

    private static int HandleFailed(FlacInstallResult.Failed failed, ILogger logger)
    {
        logger.Error("flac install failed: {Reason}", failed.Reason);
        return InstallFlacCommand.ExitFailed;
    }

    private static int HandleDisabled(ILogger logger)
    {
        logger.Warning(
            "Auto-install was disabled by configuration (this should not happen for an explicit `install-flac` invocation).");
        return InstallFlacCommand.ExitFailed;
    }

    private static int HandleUnknown(FlacInstallResult result, ILogger logger)
    {
        logger.Error("Unexpected install result type: {Type}", result.GetType().Name);
        return InstallFlacCommand.ExitFailed;
    }
}
