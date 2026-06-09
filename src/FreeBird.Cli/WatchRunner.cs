using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FreeBird.Core.Abstractions;
using FreeBird.Core.DependencyInjection;
using FreeBird.Core.Models;
using Serilog;
using Serilog.Events;

namespace FreeBird.Cli;

/// <summary>
/// T11 — pure watch execution: builds container, sets up Serilog (console + rolling daily file
/// sink), resolves <see cref="IWatchOrchestrator"/>, runs it, prints summary, returns exit code.
///
/// Mirrors <see cref="ScanRunner"/> in shape. Lives separately because watch-mode adds:
///   * a rolling daily log file (default <c>&lt;OutputDir&gt;/.freebird/logs/watch-.log</c>)
///   * a CLI-side <see cref="WatchOptions"/> → Core <see cref="FreeBird.Core.Models.WatchOptions"/> mapping
///   * a graceful + hard-abort cancellation pair (the hard token will be wired by T12)
/// </summary>
public sealed class WatchRunner
{
    public const int ExitOk = 0;
    public const int ExitFailures = 1;
    public const int ExitBadArgs = 2;
    public const int ExitCancelled = 130;

    /// <summary>
    /// Test-only hook: if non-null, the runner resolves <see cref="IWatchOrchestrator"/> from this
    /// factory (called with the constructed <see cref="ILogger"/>) instead of from the Autofac
    /// container. Used by <c>WatchRunnerTests</c> to substitute a Moq orchestrator and avoid
    /// running the real watch loop.
    /// </summary>
    public static Func<ILogger, IWatchOrchestrator>? OrchestratorFactoryOverride { get; set; }

    public async Task<int> RunAsync(WatchOptions cliOptions, CancellationToken externalToken = default)
    {
        ArgumentNullException.ThrowIfNull(cliOptions);

        // D1: --verbose and --quiet are mutually exclusive. WatchCommand already enforces this,
        // but double-check to keep the runner independently safe.
        if (cliOptions.Verbose && cliOptions.Quiet)
        {
            using var bootstrap = new LoggerConfiguration()
                .MinimumLevel.Is(LogEventLevel.Information)
                .WriteTo.Console()
                .CreateLogger();
            bootstrap.Error("--verbose and --quiet are mutually exclusive; pick one.");
            return ExitBadArgs;
        }

        if (!Directory.Exists(cliOptions.InputDir))
        {
            using var bootstrap = new LoggerConfiguration()
                .MinimumLevel.Is(LogEventLevel.Information)
                .WriteTo.Console()
                .CreateLogger();
            bootstrap.Error("Input directory not found: {Input}", cliOptions.InputDir);
            return ExitBadArgs;
        }

        Directory.CreateDirectory(cliOptions.OutputDir);

        ILogger logger = BuildLogger(cliOptions);
        try
        {
            logger.Information(
                "Watch starting. Input={Input}, Output={Output}, Integrity={Integrity}, Concurrency={Concurrency}, PollInterval={PollInterval}, StabilityChecks={Checks}, MinFileSize={MinSize}, SkipInitialScan={SkipInitial}",
                cliOptions.InputDir, cliOptions.OutputDir, cliOptions.Integrity,
                cliOptions.Concurrency, cliOptions.PollInterval, cliOptions.StabilityChecks,
                cliOptions.MinFileSize, cliOptions.SkipInitialScan);

            var coreOptions = ToCoreOptions(cliOptions);

            IWatchOrchestrator orchestrator;
            IContainer? container = null;
            ILifetimeScope? scope = null;

            try
            {
                if (OrchestratorFactoryOverride is not null)
                {
                    orchestrator = OrchestratorFactoryOverride(logger);
                }
                else
                {
                    container = BuildContainer(logger);
                    scope = container.BeginLifetimeScope();
                    orchestrator = scope.Resolve<IWatchOrchestrator>();
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

                // T11: graceful and hard tokens are the same for now — T12 wires double-Ctrl-C.
                var summary = await orchestrator.RunAsync(coreOptions, cts.Token, cts.Token).ConfigureAwait(false);

                Console.Out.WriteLine(
                    $"Watch summary: Processed={summary.Processed}, Ok={summary.Ok}, Skipped={summary.Skipped}, " +
                    $"UnknownFormat={summary.UnknownFormat}, IntegrityFailed={summary.IntegrityFailed}, " +
                    $"Errors={summary.Errors}, Duration={summary.Duration}");

                return summary.HasFailures ? ExitFailures : ExitOk;
            }
            finally
            {
                if (scope is not null) { await scope.DisposeAsync().ConfigureAwait(false); }
                if (container is not null) { await container.DisposeAsync().ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Watch cancelled.");
            return ExitCancelled;
        }
        catch (DirectoryNotFoundException ex)
        {
            logger.Error(ex, "Directory not found");
            return ExitBadArgs;
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Unhandled error");
            return ExitFailures;
        }
        finally
        {
            // Flush + close all sinks (especially the rolling file sink).
            (logger as IDisposable)?.Dispose();
            Log.CloseAndFlush();
        }
    }

    private static ILogger BuildLogger(WatchOptions cliOptions)
    {
        var level = cliOptions.Quiet ? LogEventLevel.Warning
                  : cliOptions.Verbose ? LogEventLevel.Debug
                  : LogEventLevel.Information;

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        if (!cliOptions.NoLogFile)
        {
            var logPath = ResolveLogFilePath(cliOptions);
            var logDir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            config = config.WriteTo.File(
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }

        return config.CreateLogger();
    }

    /// <summary>
    /// Default log path: <c>&lt;OutputDir&gt;/.freebird/logs/watch-.log</c>.
    /// Serilog with <see cref="RollingInterval.Day"/> inserts the date before the extension,
    /// producing e.g. <c>watch-20260610.log</c>.
    /// </summary>
    private static string ResolveLogFilePath(WatchOptions cliOptions)
    {
        if (!string.IsNullOrWhiteSpace(cliOptions.LogFilePath))
        {
            return cliOptions.LogFilePath!;
        }
        return Path.Combine(cliOptions.OutputDir, ".freebird", "logs", "watch-.log");
    }

    private static FreeBird.Core.Models.WatchOptions ToCoreOptions(WatchOptions cli) => new(
        InputDir: cli.InputDir,
        OutputDir: cli.OutputDir,
        Integrity: cli.Integrity,
        Concurrency: cli.Concurrency,
        Collision: cli.Collision,
        PollInterval: cli.PollInterval,
        StabilityChecks: cli.StabilityChecks,
        MinFileSizeBytes: cli.MinFileSize,
        SkipInitialScan: cli.SkipInitialScan);

    private static IContainer BuildContainer(ILogger logger)
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        builder.RegisterInstance(logger).As<ILogger>().SingleInstance();
        return builder.Build();
    }
}
