using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FreeBird.Core.Abstractions;
using FreeBird.Core.DependencyInjection;
using FreeBird.Core.Models;
using Serilog;
using Serilog.Core;
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

    /// <summary>
    /// Test-only hook: if non-null, the runner uses this coordinator instead of constructing its
    /// own. Lets tests inject a pre-signalled coordinator to verify the exit-code 130 mapping
    /// without involving real OS signals.
    /// </summary>
    public static Func<ILogger, CancellationCoordinator>? CoordinatorFactoryOverride { get; set; }

    /// <summary>
    /// Test-only hook (v2 T18): additional Serilog sinks appended to the runner's
    /// <see cref="LoggerConfiguration"/> alongside the default console (and optional file) sinks.
    /// Used by <c>CycleLockE2ETests</c> to capture every emitted <c>LogEvent</c> via an
    /// in-memory sink and assert on the debounced WARN/DEBUG skip-poll sequence.
    /// Defaults to an empty list — production behavior is unaffected.
    /// </summary>
    public static List<ILogEventSink> AdditionalLogSinks { get; } = new();

    /// <summary>
    /// Test-only hook (v2 T18): if non-null, invoked against the Autofac
    /// <see cref="ContainerBuilder"/> after <c>CoreModule</c> + the logger instance have
    /// been registered but before <c>Build()</c>. Lets tests override individual
    /// registrations (e.g. swap in a slow <see cref="IFileProcessor"/>) while keeping
    /// the rest of the real wiring intact.
    /// </summary>
    public static Action<ContainerBuilder>? AdditionalContainerSetup { get; set; }

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
            CancellationCoordinator? coordinator = null;
            IDisposable? sigintRegistration = null;
            IDisposable? sigtermRegistration = null;

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

                coordinator = CoordinatorFactoryOverride is not null
                    ? CoordinatorFactoryOverride(logger)
                    : new CancellationCoordinator(TimeProvider.System, logger);

                // Wire OS signals → coordinator. The external token is intentionally NOT
                // bridged into coordinator.OnCancelRequested() — System.CommandLine 3.x installs
                // its own SIGINT handler that cancels the action delegate's CT, so bridging it
                // here would double-count signals (once via the bridge, once via our own
                // Console.CancelKeyPress handler) and immediately escalate to hard abort,
                // bypassing the 5s graceful drain. Instead, link the external token into the
                // graceful token so an external host CTS still stops the orchestrator gracefully.
                sigintRegistration = SubscribeSigint(coordinator);
                sigtermRegistration = SubscribeSigterm(coordinator);
                using var linkedGraceful = CancellationTokenSource.CreateLinkedTokenSource(
                    coordinator.Graceful, externalToken);

                try
                {
                    var summary = await orchestrator.RunAsync(coreOptions, linkedGraceful.Token, coordinator.Hard).ConfigureAwait(false);

                    Console.Out.WriteLine(
                        $"Watch summary: Processed={summary.Processed}, Ok={summary.Ok}, Skipped={summary.Skipped}, " +
                        $"UnknownFormat={summary.UnknownFormat}, IntegrityFailed={summary.IntegrityFailed}, " +
                        $"Errors={summary.Errors}, Duration={summary.Duration}");

                    if (coordinator.SignalCount > 0) { return ExitCancelled; }
                    return summary.HasFailures ? ExitFailures : ExitOk;
                }
                catch (OperationCanceledException) when (coordinator.SignalCount > 0)
                {
                    // Hard-cancel mid-flight. WatchOrchestrator normally returns a partial summary
                    // on cancel; this catch defends against any cancel-throw path that still leaks.
                    logger.Warning("Watch aborted by user signal.");
                    return ExitCancelled;
                }
            }
            finally
            {
                sigintRegistration?.Dispose();
                sigtermRegistration?.Dispose();
                coordinator?.Dispose();
                if (scope is not null) { await scope.DisposeAsync().ConfigureAwait(false); }
                if (container is not null) { await container.DisposeAsync().ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException)
        {
            // An OCE escaped without a signal having been received — this would be a pre-RunAsync
            // cancellation (e.g. container build aborted). Map to generic failure, not 130,
            // because no SIGINT/SIGTERM was actually delivered to us. The signal-driven path is
            // handled by the inner `when (coordinator.SignalCount > 0)` catch above.
            logger.Warning("Watch cancelled outside the signal path.");
            return ExitFailures;
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

    /// <summary>
    /// Subscribes to <see cref="Console.CancelKeyPress"/> (Ctrl-C / SIGINT on all platforms).
    /// We set <c>e.Cancel = true</c> so the default behavior (immediate process termination) is
    /// suppressed and our coordinator's state machine gets to run. Returns an IDisposable that
    /// unsubscribes the handler when the watch run completes.
    /// </summary>
    private static IDisposable SubscribeSigint(CancellationCoordinator coordinator)
    {
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            coordinator.OnCancelRequested();
        };
        Console.CancelKeyPress += handler;
        return new Unsubscriber(() => Console.CancelKeyPress -= handler);
    }

    /// <summary>
    /// Subscribes to SIGTERM via <see cref="PosixSignalRegistration"/>. SIGTERM is conventionally
    /// "stop now" for services — no grace period — so we route it to the hard signal path.
    /// </summary>
    private static IDisposable SubscribeSigterm(CancellationCoordinator coordinator)
    {
        var reg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            coordinator.OnHardSignalRequested();
        });
        return reg;
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _action;
        public Unsubscriber(Action action) { _action = action; }
        public void Dispose()
        {
            var a = Interlocked.Exchange(ref _action, null);
            a?.Invoke();
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

        // T18 test hook: append any extra sinks (e.g. InMemoryLogSink). Snapshot to avoid
        // races with tests mutating the list mid-build.
        foreach (var extraSink in AdditionalLogSinks.ToArray())
        {
            config = config.WriteTo.Sink(extraSink);
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
        // T18 test hook: let tests override individual registrations after the default
        // wiring is in place (Autofac's last-registration-wins behavior makes the override
        // resolve in preference to the CoreModule default).
        AdditionalContainerSetup?.Invoke(builder);
        return builder.Build();
    }
}
