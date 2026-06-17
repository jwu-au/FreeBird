using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using FreeBird.Cli.Service;
using FreeBird.Core.Models;
using FreeBird.Core.Service;
using FreeBird.Core.Watch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// T22 — Windows-only host body for <see cref="RunCommand.RunHostAsync"/>. Compiled on Windows
/// only (see FreeBird.Cli.csproj); it references the Windows-only
/// <see cref="WindowsServiceLifetimeHostBuilderExtensions.UseWindowsService(IHostBuilder)"/> and
/// the EventLog Serilog sink, which are restored on Windows alone.
///
/// <para>Autofac → MS.DI bridge: the watch pipeline is registered in Autofac
/// (<see cref="ServiceHostBuilder.BuildContainer(ILogger, RootConfig)"/>), so we build that
/// container, resolve the single <see cref="WatchSupervisor"/> it composes, and register that
/// instance plus the <see cref="RootConfig"/> into the host's MS.DI alongside
/// <see cref="WatchHostedService"/>. Serilog is bridged via <c>AddSerilog</c>. This keeps a single
/// source of truth for the watch wiring (Autofac) while letting the generic host own the
/// SCM lifetime.</para>
/// </summary>
public static partial class RunCommand
{
    public static partial bool IsRunningAsWindowsService() => WindowsServiceHelpers.IsWindowsService();

    [SupportedOSPlatform("windows")]
    private static partial async Task<int> RunHostAsync(
        RootConfig config, string configPath, CancellationToken ct)
    {
        // Resolve the final Serilog file-sink path (with §2.4 fallback) using the same IFileSystem
        // abstraction LogPathResolver uses. The watch.log_file knob feeds it; null → ProgramData.
        var logResolver = new LogPathResolver(new System.IO.Abstractions.FileSystem());
        var resolution = logResolver.Resolve(config.Watch.LogFile);
        var logDir = Path.GetDirectoryName(resolution.FinalPath);
        if (!string.IsNullOrEmpty(logDir))
        {
            Directory.CreateDirectory(logDir);
        }

        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: resolution.FinalPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.EventLog(source: "FreeBird", logName: "Application", restrictedToMinimumLevel: LogEventLevel.Warning)
            .CreateLogger();

        if (resolution.FellBack)
        {
            serilogLogger.Warning(
                "Configured log path was unusable; falling back to {FinalPath}. Reason: {Reason}",
                resolution.FinalPath, resolution.Reason);
        }

        // Build the Autofac watch container and resolve the supervisor the watch loop needs.
        using var container = ServiceHostBuilder.BuildContainer(serilogLogger, config);
        var supervisor = container.Resolve<WatchSupervisor>();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging(lb =>
        {
            lb.ClearProviders();
            lb.AddSerilog(serilogLogger, dispose: false);
        });
        builder.Services.AddSingleton(supervisor);
        builder.Services.AddSingleton(config);
        builder.Services.AddHostedService<WatchHostedService>();
        builder.UseWindowsService();

        var host = builder.Build();
        try
        {
            await host.RunAsync(ct).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            serilogLogger.Dispose();
        }
    }
}
