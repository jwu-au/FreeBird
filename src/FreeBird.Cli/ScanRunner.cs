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

    public static async Task<int> RunAsync(
        string inputDir,
        string outputDir,
        IntegrityLevel integrity,
        int concurrency,
        CollisionPolicy collision,
        bool verbose,
        CancellationToken externalToken = default)
    {
        ILogger logger = BuildLogger(verbose);
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

            var orchestrator = scope.Resolve<IScanOrchestrator>();
            var options = new ScanOptions(
                Path.GetFullPath(inputDir),
                Path.GetFullPath(outputDir),
                integrity,
                Math.Max(1, concurrency),
                collision);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
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

    private static ILogger BuildLogger(bool verbose)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static IContainer BuildContainer(ILogger logger)
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        builder.RegisterInstance(logger).As<ILogger>().SingleInstance();
        return builder.Build();
    }
}
