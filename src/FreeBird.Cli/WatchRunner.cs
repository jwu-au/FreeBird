using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FreeBird.Cli.DependencyInjection;
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

        // T16: v3.4 supervisor path validates each input directory itself (invalid entries
        // become born-DEAD tasks logged at WARN). No eager pre-check here.
        Directory.CreateDirectory(cliOptions.OutputDir);

        ILogger logger = BuildLogger(cliOptions);
        try
        {
            logger.Information(
                "Watch starting. Input={Input}, Output={Output}, Integrity={Integrity}, Concurrency={Concurrency}, PollInterval={PollInterval}, StabilityChecks={Checks}, MinFileSize={MinSize}, SkipInitialScan={SkipInitial}",
                cliOptions.InputDirs[0], cliOptions.OutputDir, cliOptions.Integrity,
                cliOptions.Concurrency, cliOptions.PollInterval, cliOptions.StabilityChecks,
                cliOptions.MinFileSize, cliOptions.SkipInitialScan);

            var coreOptions = ToCoreOptions(cliOptions);

            IContainer? container = null;
            ILifetimeScope? scope = null;
            CancellationCoordinator? coordinator = null;
            IDisposable? sigintRegistration = null;
            IDisposable? sigtermRegistration = null;

            try
            {
                // T16: always build the container, even when OrchestratorFactoryOverride is set.
                // The supervisor path resolves WatchSupervisor + Func<WatchInput, WatchTask> from
                // the container; the override (if any) is wired in as the IWatchOrchestrator
                // registration so each WatchTask's Func<IWatchOrchestrator> resolves the mock.
                //
                // T15: resolve flac provisioning options from CLI flags + env vars BEFORE
                // building the container so the FlacResolverOptions override sees the final
                // values (CLI flag > env var > default).
                var flacOptions = FlacOptionsBinder.Resolve(
                    cliOptions.FlacBin,
                    cliOptions.FlacUrl,
                    cliOptions.NoAutoDownload,
                    Environment.GetEnvironmentVariable("FREEBIRD_FLAC_URL"),
                    Environment.GetEnvironmentVariable("FREEBIRD_NO_AUTO_DOWNLOAD"));

                container = BuildContainer(logger, flacOptions);
                scope = container.BeginLifetimeScope();

                // T13 / parity with ScanRunner D2+D3+D4: probe flac at startup so we can
                // fail fast for --integrity l3 when the binary is missing, and emit a
                // one-time warning for --integrity auto's silent L1 degradation.
                //
                // Skipped when OrchestratorFactoryOverride is set: tests using the override
                // typically don't care about flac, and forcing them to register IFlacProbe
                // would break the existing minimal-mock contract.
                if (OrchestratorFactoryOverride is null)
                {
                    var probe = scope.Resolve<IFlacProbe>();
                    var flacAvailable = await probe.IsAvailableAsync(externalToken).ConfigureAwait(false);

                    if (cliOptions.Integrity == IntegrityLevel.L3 && !flacAvailable)
                    {
                        logger.Error(
                            "--integrity l3 requires the 'flac' binary on PATH (install via `brew install flac` / `apt install flac`).");
                        return ExitBadArgs;
                    }

                    if (cliOptions.Integrity == IntegrityLevel.Auto && !flacAvailable)
                    {
                        logger.Warning(
                            "--integrity auto: 'flac' binary not found on PATH; falling back to L1 (structural check only) for FLAC files. Install flac for full PCM-MD5 verification.");
                    }
                }

                var supervisor = scope.Resolve<FreeBird.Core.Watch.WatchSupervisor>();
                var taskFactory = scope.Resolve<Func<FreeBird.Core.Watch.WatchInput, FreeBird.Core.Watch.WatchTask>>();

                var (validInputs, _invalidInputs) = supervisor.ParseAndValidateInputs(cliOptions.InputDirs);
                if (validInputs.Count == 0)
                {
                    // Per spec §2.5 watch with all-invalid inputs is a born-DEAD no-op, not a
                    // hard error. The supervisor has already logged each invalid entry at WARN.
                    logger.Warning("watch: no valid input directories; nothing to watch — exiting cleanly");
                    Console.Out.WriteLine(
                        "Watch summary: Processed=0, Ok=0, Skipped=0, UnknownFormat=0, IntegrityFailed=0, Errors=0, Duration=00:00:00");
                    return ExitOk;
                }

                var tasks = supervisor.CreateTasks(validInputs);

                coordinator = CoordinatorFactoryOverride is not null
                    ? CoordinatorFactoryOverride(logger)
                    : new CancellationCoordinator(TimeProvider.System, logger);

                // Wire OS signals → coordinator. The external token is intentionally NOT
                // bridged into coordinator.OnCancelRequested() — System.CommandLine 3.x installs
                // its own SIGINT handler that cancels the action delegate's CT, so bridging it
                // here would double-count signals (once via the bridge, once via our own
                // Console.CancelKeyPress handler) and immediately escalate to hard abort,
                // bypassing the 5s graceful drain. Instead, link the external token into the
                // graceful token so an external host CTS still stops the supervisor gracefully.
                sigintRegistration = SubscribeSigint(coordinator);
                sigtermRegistration = SubscribeSigterm(coordinator);
                using var linkedGraceful = CancellationTokenSource.CreateLinkedTokenSource(
                    coordinator.Graceful, coordinator.Hard, externalToken);

                // T16: probe stops when supervisor finishes naturally OR when an external
                // cancel fires. Linked CTS lets the runner explicitly stop the probe after
                // supervisor.RunAsync returns without waiting for the user to Ctrl-C again.
                using var probeStopCts = CancellationTokenSource.CreateLinkedTokenSource(linkedGraceful.Token);

                var timeProvider = scope.Resolve<TimeProvider>();
                var healthProbe = new FreeBird.Core.Watch.HealthProbe(
                    supervisor, timeProvider, logger, coreOptions.HealthProbeInterval);
                var probeTask = healthProbe.RunAsync(probeStopCts.Token);

                try
                {
                    var summary = await supervisor.RunAsync(tasks, coreOptions, linkedGraceful.Token).ConfigureAwait(false);

                    Console.Out.WriteLine(
                        $"Watch summary: Processed={summary.Processed}, Ok={summary.Ok}, Skipped={summary.Skipped}, " +
                        $"UnknownFormat={summary.UnknownFormat}, IntegrityFailed={summary.IntegrityFailed}, " +
                        $"Errors={summary.Errors}, Duration={summary.Duration}");

                    if (coordinator.SignalCount > 0) { return ExitCancelled; }
                    return summary.HasFailures ? ExitFailures : ExitOk;
                }
                catch (OperationCanceledException) when (coordinator.SignalCount > 0)
                {
                    // Hard-cancel mid-flight. Supervisor normally returns a partial summary
                    // on cancel; this catch defends against any cancel-throw path that still leaks.
                    logger.Warning("Watch aborted by user signal.");
                    return ExitCancelled;
                }
                finally
                {
                    // Stop the probe ticker and wait for it to drain. RunAsync swallows OCE,
                    // but defend against a leaking exception so the finally never throws.
                    probeStopCts.Cancel();
                    try { await probeTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { logger.Warning(ex, "health probe drain raised; ignored"); }
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
        InputDirs: cli.InputDirs,
        OutputDir: cli.OutputDir,
        Integrity: cli.Integrity,
        Concurrency: cli.Concurrency,
        Collision: cli.Collision,
        PollInterval: cli.PollInterval,
        StabilityChecks: cli.StabilityChecks,
        MinFileSizeBytes: cli.MinFileSize,
        SkipInitialScan: cli.SkipInitialScan)
    {
        NamingTemplate = cli.NamingTemplate,
        Offline = cli.Offline,
        ApiTimeoutSeconds = cli.ApiTimeoutSeconds,
        ApiRateLimit = cli.ApiRateLimit,
        WriteTags = cli.WriteTags,
    };

    private static IContainer BuildContainer(ILogger logger, FlacOptionsBinder.EffectiveFlacOptions flacOptions)
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        builder.RegisterModule<CliServiceModule>();
        builder.RegisterInstance(logger).As<ILogger>().SingleInstance();

        // T15: override CoreModule's default FlacResolverOptions factory with one populated
        // from the CLI/env-resolved values. Autofac's last-registration-wins makes this
        // override resolve in preference to the in-module default. AppBaseDirectory is
        // still pulled from IAppBaseDirectoryProvider so AppContext.BaseDirectory is honored.
        builder.Register(c => new FreeBird.Core.Provisioning.FlacResolverOptions
        {
            AppBaseDirectory = c.Resolve<FreeBird.Core.Provisioning.IAppBaseDirectoryProvider>().GetBaseDirectory(),
            FlacBinOverride = flacOptions.FlacBinOverride,
            MetaflacBinOverride = null,
            AutoInstallUrl = flacOptions.AutoInstallUrl,
            DisableAutoInstall = flacOptions.DisableAutoInstall,
        }).AsSelf().SingleInstance();

        // T16: if OrchestratorFactoryOverride is set, register the resulting mock as the
        // IWatchOrchestrator. The supervisor's per-task WatchTask resolves a
        // Func<IWatchOrchestrator> from the container (auto-synthesised by Autofac), so this
        // override flows through to every WatchTask without changing the test contract.
        if (OrchestratorFactoryOverride is not null)
        {
            var ovr = OrchestratorFactoryOverride;
            builder.Register(_ => ovr(logger))
                   .As<IWatchOrchestrator>()
                   .InstancePerDependency();
        }

        // T18 test hook: let tests override individual registrations after the default
        // wiring is in place (Autofac's last-registration-wins behavior makes the override
        // resolve in preference to the CoreModule default).
        AdditionalContainerSetup?.Invoke(builder);
        return builder.Build();
    }
}
