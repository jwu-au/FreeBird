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
/// Pure scan execution: builds container, runs orchestrator, returns exit code.
/// Separated from Program.cs so it can be unit-tested.
/// </summary>
public static class ScanRunner
{
    public const int ExitOk = 0;
    public const int ExitFailures = 1;
    public const int ExitBadArgs = 2;
    public const int ExitCancelled = 130;

    /// <summary>
    /// Test-only hook (v3 T13): if non-null, invoked against the Autofac
    /// <see cref="ContainerBuilder"/> after <c>CoreModule</c> + the logger instance have
    /// been registered but before <c>Build()</c>. Mirrors the same affordance on
    /// <c>WatchRunner.AdditionalContainerSetup</c>. Used by v1/v2 E2E tests to override
    /// <c>IFileNamer</c> back to <c>StemBasedFileNamer</c> when stem-based filenames
    /// (e.g. <c>42-song.mp3</c>) are required — the v3 default <c>MetadataAwareFileNamer</c>
    /// produces <c>{musicId}.{ext}</c> names that those legacy tests do not expect.
    /// </summary>
    public static Action<ContainerBuilder>? AdditionalContainerSetup { get; set; }

    public static async Task<int> RunAsync(
        string inputDir,
        string outputDir,
        IntegrityLevel integrity,
        int concurrency,
        CollisionPolicy collision,
        bool verbose,
        bool quiet = false,
        CancellationToken externalToken = default)
    {
        // D1: --verbose and --quiet are mutually exclusive — caller must pick one.
        // Built before the logger so we can use a default Information-level logger to emit the error.
        if (verbose && quiet)
        {
            using var bootstrap = new LoggerConfiguration()
                .MinimumLevel.Is(LogEventLevel.Information)
                .WriteTo.Console()
                .CreateLogger();
            bootstrap.Error("--verbose and --quiet are mutually exclusive; pick one.");
            return ExitBadArgs;
        }

        ILogger logger = BuildLogger(verbose, quiet);
        try
        {
            if (!Directory.Exists(inputDir))
            {
                logger.Error("Input directory not found: {Input}", inputDir);
                return ExitBadArgs;
            }

            Directory.CreateDirectory(outputDir);

            await using var container = BuildContainer(logger);
            await using var scope = container.BeginLifetimeScope();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

            // D2 + D3 + D4: Probe flac at startup (not lazily) so we can fail fast for --integrity l3
            // when the binary is missing, and emit a one-time warning for --integrity auto fallback.
            var probe = scope.Resolve<IFlacProbe>();
            var flacAvailable = await probe.IsAvailableAsync(cts.Token).ConfigureAwait(false);

            if (integrity == IntegrityLevel.L3 && !flacAvailable)
            {
                logger.Error(
                    "--integrity l3 requires the 'flac' binary on PATH (install via `brew install flac` / `apt install flac`).");
                return ExitBadArgs;
            }

            if (integrity == IntegrityLevel.Auto && !flacAvailable)
            {
                logger.Warning(
                    "--integrity auto: 'flac' binary not found on PATH; falling back to L1 (structural check only) for FLAC files. Install flac for full PCM-MD5 verification.");
            }

            var orchestrator = scope.Resolve<IScanOrchestrator>();
            var options = new ScanOptions(
                Path.GetFullPath(inputDir),
                Path.GetFullPath(outputDir),
                integrity,
                Math.Max(1, concurrency),
                collision);

            ConsoleCancelEventHandler handler = (_, e) =>
            {
                e.Cancel = true;
                logger.Warning("Ctrl-C received — cancelling...");
                cts.Cancel();
            };
            Console.CancelKeyPress += handler;

            try
            {
                var summary = await orchestrator.RunAsync(options, cts.Token);
                return summary.HasFailures ? ExitFailures : ExitOk;
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }
        catch (OperationCanceledException)
        {
            logger.Warning("Scan cancelled.");
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
            (logger as IDisposable)?.Dispose();
        }
    }

    private static ILogger BuildLogger(bool verbose, bool quiet)
    {
        // quiet wins over verbose by design — but mutual exclusion is enforced at the entry point,
        // so reaching here with both true should not happen.
        var level = quiet ? LogEventLevel.Warning
                  : verbose ? LogEventLevel.Debug
                  : LogEventLevel.Information;
        return new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static IContainer BuildContainer(ILogger logger)
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        builder.RegisterInstance(logger).As<ILogger>().SingleInstance();
        // T13 test hook (mirrors WatchRunner): let tests override individual registrations
        // after the default wiring is in place. Autofac last-registration-wins.
        AdditionalContainerSetup?.Invoke(builder);
        return builder.Build();
    }
}
