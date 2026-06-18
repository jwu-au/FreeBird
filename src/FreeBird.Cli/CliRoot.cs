using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Autofac;
using FreeBird.Cli.Commands.Service;
using FreeBird.Cli.Service;
using FreeBird.Core.Models;
using FreeBird.Core.Service;
using Serilog;

namespace FreeBird.Cli;

/// <summary>
/// Builds the root command (scan + watch subcommands) and invokes parsing.
/// Factored out of Program.cs so tests can invoke the CLI in-process.
/// </summary>
public static class CliRoot
{
    public static Task<int> InvokeAsync(string[] args)
    {
        var root = Build();
        var parseResult = root.Parse(args);
        return parseResult.InvokeAsync();
    }

    public static RootCommand Build()
    {
        // T14/T15 (v3.4): scan accepts one or more positional input directories.
        // OneOrMore arity preserves backward compat (single dir still works) and
        // System.CommandLine treats subsequent `--option`-prefixed tokens as the boundary.
        var inputArg = new Argument<List<string>>("input-dirs")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "One or more directories to scan for .uc / .uc! cache files.",
        };

        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "Directory to write decoded audio files.",
            Required = true,
        };

        var integrityOpt = new Option<IntegrityLevel>("--integrity")
        {
            Description = "Integrity check level: auto | l1 | l3 | off.",
            DefaultValueFactory = _ => IntegrityLevel.Auto,
        };

        var concurrencyOpt = new Option<int>("--concurrency")
        {
            Description = "Max parallel files being processed.",
            DefaultValueFactory = _ => 4,
        };

        var collisionOpt = new Option<CollisionPolicy>("--on-collision")
        {
            Description = "What to do when an output file already exists: skip | overwrite.",
            DefaultValueFactory = _ => CollisionPolicy.Skip,
        };

        var verboseOpt = new Option<bool>("--verbose", "-v")
        {
            Description = "Verbose logging (Debug level).",
        };

        var quietOpt = new Option<bool>("--quiet", "-q")
        {
            Description = "Quiet logging (Warning level and above only). Mutually exclusive with --verbose.",
        };

        // T19b — v3 metadata flags.
        var namingTemplateOpt = new Option<string>("--naming-template")
        {
            Description = "Filename template using {artist} {title} {album} {musicId}.",
            DefaultValueFactory = _ => MetadataDefaults.NamingTemplate,
        };

        var offlineOpt = new Option<bool>("--offline")
        {
            Description = "Skip NetEase API; use musicId fallback naming.",
        };

        var apiTimeoutOpt = new Option<int>("--api-timeout")
        {
            Description = "NetEase API request timeout in seconds (1–300).",
            DefaultValueFactory = _ => MetadataDefaults.ApiTimeoutSeconds,
        };

        var apiRateLimitOpt = new Option<int>("--api-rate-limit")
        {
            Description = "Max NetEase API calls per second (0–100, 0 = unlimited).",
            DefaultValueFactory = _ => MetadataDefaults.ApiRateLimit,
        };

        var writeTagsOpt = new Option<bool>("--write-tags")
        {
            Description = "Write metadata tags (artist/title/album) into decoded audio files. Default: true.",
            DefaultValueFactory = _ => true,
        };

        // v3.3: opt-out alias for --write-tags. If true, suppresses tag writing
        // even when --write-tags would otherwise default to true.
        var noWriteTagsOpt = new Option<bool>("--no-write-tags")
        {
            Description = "Disable tag writing (overrides default --write-tags=true).",
        };

        // T11 — v3.1 flac provisioning flags (consumed in T15 by ScanRunner/WatchRunner via FlacOptionsBinder).
        var flacBinOpt = new Option<string?>("--flac-bin")
        {
            Description = "Explicit path to flac binary (overrides probe order).",
        };

        var flacUrlOpt = new Option<string?>("--flac-url")
        {
            Description = "Override download URL for Windows auto-install (advanced).",
            Hidden = true,
        };

        var noAutoDownloadOpt = new Option<bool>("--no-auto-download")
        {
            Description = "Disable Windows auto-install of flac when missing.",
        };

        var scanCommand = new Command("scan",
            "Decode all .uc/.uc! files in one or more <input-dirs>, write decoded audio to --output.")
        {
            inputArg, outputOpt, integrityOpt, concurrencyOpt, collisionOpt, verboseOpt, quietOpt,
            namingTemplateOpt, offlineOpt, apiTimeoutOpt, apiRateLimitOpt, writeTagsOpt, noWriteTagsOpt,
            flacBinOpt, flacUrlOpt, noAutoDownloadOpt,
        };

        scanCommand.SetAction(async (parseResult, ct) =>
        {
            // T14/T15 (v3.4): positional argument is now List<string> (OneOrMore arity).
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            var integrity = parseResult.GetValue(integrityOpt);
            var concurrency = parseResult.GetValue(concurrencyOpt);
            var collision = parseResult.GetValue(collisionOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var quiet = parseResult.GetValue(quietOpt);
            var namingTemplate = parseResult.GetValue(namingTemplateOpt) ?? MetadataDefaults.NamingTemplate;
            var offline = parseResult.GetValue(offlineOpt);
            var apiTimeout = parseResult.GetValue(apiTimeoutOpt);
            var apiRateLimit = parseResult.GetValue(apiRateLimitOpt);
            // v3.3: combine --write-tags (default true) with --no-write-tags opt-out.
            // --no-write-tags suppresses tag writing regardless of --write-tags value.
            var writeTags = parseResult.GetValue(writeTagsOpt) && !parseResult.GetValue(noWriteTagsOpt);
            // T15: flac provisioning flags routed through FlacOptionsBinder inside ScanRunner.
            var flacBin = parseResult.GetValue(flacBinOpt);
            var flacUrl = parseResult.GetValue(flacUrlOpt);
            var noAutoDownload = parseResult.GetValue(noAutoDownloadOpt);

            var validationExit = MetadataFlagsValidator.Validate(
                namingTemplate, apiTimeout, apiRateLimit, Console.Error);
            if (validationExit is not null)
            {
                return validationExit.Value;
            }

            // T14/T15: `input` is List<string>; ScanRunner accepts IReadOnlyList<string>.
            return await ScanRunner.RunAsync(
                input, output, integrity, concurrency, collision, verbose, quiet,
                namingTemplate, offline, apiTimeout, apiRateLimit, writeTags,
                flacBin, flacUrl, noAutoDownload,
                ct);
        });

        // T22 (v3.5): wire the `service` command tree inline, mirroring the scan/watch/install-flac
        // precedent above. Seven visible leaves + one hidden `run` (the SCM entrypoint). Every
        // leaf's SetAction does the non-Windows short-circuit FIRST (ShortCircuitNonWindows) before
        // building any container or touching Windows-only resolution, so macOS / Linux get the
        // friendly Windows-only message instead of a NotSupported* throw.
        var serviceCommand = BuildServiceCommand();

        var root = new RootCommand("FreeBird — NetEase Music cache decoder")
        {
            scanCommand,
            WatchCommand.Build(),
            InstallFlacCommand.Build(),
            serviceCommand,
        };

        return root;
    }

    /// <summary>
    /// The friendly message shown when any <c>fb service</c> leaf is invoked on a non-Windows host.
    /// Kept in sync with <see cref="RunCommand"/>'s internal copy.
    /// </summary>
    private const string ServiceWindowsOnlyMessage =
        "ERROR: 'fb service' is Windows-only. See README §Service for launchd / systemd snippets.";

    /// <summary>Default SCM short name used by start/stop/restart/status/uninstall.</summary>
    private const string DefaultServiceName = "FreeBird";

    /// <summary>
    /// Writes the Windows-only message to stderr and returns true when running off-Windows, so each
    /// leaf can short-circuit as its FIRST action with <c>if (ShortCircuitNonWindows()) return 1;</c>.
    /// </summary>
    private static bool ShortCircuitNonWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(ServiceWindowsOnlyMessage);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Builds a console-sink Serilog logger for the service leaves' container/config needs. The
    /// hosted-run path (RunCommand) builds its own file + EventLog logger instead.
    /// </summary>
    private static Serilog.ILogger BuildServiceLeafLogger() =>
        new Serilog.LoggerConfiguration().WriteTo.Console().CreateLogger();

    /// <summary>
    /// Builds the <c>service</c> root and its eight children inline (additive to <see cref="Build"/>).
    /// Children are added via <see cref="System.CommandLine.Command.Subcommands"/>; <c>run</c> is
    /// marked Hidden so it stays reachable (<c>fb service run --help</c>) without appearing in help.
    /// </summary>
    private static Command BuildServiceCommand()
    {
        // --- shared options (each leaf takes only the ones it needs) ---
        var configOpt = new Option<string>("--config")
        {
            Description = "Path to the service config JSON. Defaults to %ProgramData%\\FreeBird\\config.json.",
            DefaultValueFactory = _ => ConfigDefaults.DefaultConfigPath,
        };
        var nameOpt = new Option<string>("--name")
        {
            Description = "SCM service short name.",
            DefaultValueFactory = _ => DefaultServiceName,
        };
        var serviceAccountOpt = new Option<string?>("--service-account")
        {
            Description = "Windows account to run the service as (default: LocalSystem).",
        };
        var servicePasswordOpt = new Option<string?>("--service-password")
        {
            Description = "Password for --service-account (prompted / FB_SERVICE_PASSWORD if omitted).",
        };
        var yesOpt = new Option<bool>("--yes", "-y")
        {
            Description = "Skip the uninstall confirmation prompt.",
        };
        var timeoutOpt = new Option<int>("--timeout")
        {
            Description = "Seconds to wait for the SCM start/stop transition.",
            DefaultValueFactory = _ => 30,
        };
        var outputOpt = new Option<string?>("--output")
        {
            Description = "Path to write the config file to (init). Defaults to %ProgramData%\\FreeBird\\config.json.",
        };
        var forceOpt = new Option<bool>("--force")
        {
            Description = "Overwrite an existing config file (init).",
        };

        // --- init ---
        var initCmd = new Command("init", "Write a default FreeBird service config file.")
        {
            outputOpt, forceOpt,
        };
        initCmd.SetAction(parseResult =>
        {
            if (ShortCircuitNonWindows())
            {
                return 1;
            }
            // Expand any env tokens (e.g. %ProgramData%, %USERPROFILE%) in --output, and
            // fall back to the expanded ProgramData default when --output is omitted, so
            // `init` writes to the same real path that install/run later read from.
            return InitCommand.Handle(
                ServiceConfigPath.Resolve(parseResult.GetValue(outputOpt)),
                parseResult.GetValue(forceOpt),
                new FileSystem(),
                Console.Out,
                Console.Error);
        });

        // --- install ---
        var installCmd = new Command("install", "Install FreeBird as a Windows service.")
        {
            configOpt, serviceAccountOpt, servicePasswordOpt,
        };
        installCmd.SetAction(async (parseResult, ct) =>
        {
            if (ShortCircuitNonWindows())
            {
                return 1;
            }
            var logger = BuildServiceLeafLogger();
            using var container = ServiceHostBuilder.BuildContainer(logger);
            using var scope = container.BeginLifetimeScope();
            var args = new InstallArgs(
                ServiceConfigPath.Resolve(parseResult.GetValue(configOpt)),
                parseResult.GetValue(serviceAccountOpt),
                parseResult.GetValue(servicePasswordOpt));
            return await InstallCommand.HandleAsync(
                args,
                scope.Resolve<IElevationChecker>(),
                new JsonConfigLoader(logger),
                scope.Resolve<IServiceController>(),
                scope.Resolve<IEventLogWriter>(),
                Console.Out,
                Console.Error,
                Console.In,
                ct);
        });

        // --- uninstall ---
        var uninstallCmd = new Command("uninstall", "Uninstall the FreeBird Windows service.")
        {
            nameOpt, yesOpt, timeoutOpt,
        };
        uninstallCmd.SetAction(async (parseResult, ct) =>
        {
            if (ShortCircuitNonWindows())
            {
                return 1;
            }
            var logger = BuildServiceLeafLogger();
            using var container = ServiceHostBuilder.BuildContainer(logger);
            using var scope = container.BeginLifetimeScope();
            var args = new UninstallCommand.UninstallArgs(
                parseResult.GetValue(yesOpt),
                parseResult.GetValue(nameOpt)!,
                TimeSpan.FromSeconds(parseResult.GetValue(timeoutOpt)));
            return await UninstallCommand.HandleAsync(
                args,
                scope.Resolve<IElevationChecker>(),
                scope.Resolve<IServiceController>(),
                scope.Resolve<IEventLogWriter>(),
                Console.In,
                Console.Out,
                Console.Error,
                ct);
        });

        // --- start ---
        var startCmd = new Command("start", "Start the FreeBird Windows service.")
        {
            nameOpt, timeoutOpt,
        };
        startCmd.SetAction(async (parseResult, ct) =>
        {
            if (ShortCircuitNonWindows())
            {
                return 1;
            }
            var logger = BuildServiceLeafLogger();
            using var container = ServiceHostBuilder.BuildContainer(logger);
            using var scope = container.BeginLifetimeScope();
            return await StartCommand.HandleAsync(
                parseResult.GetValue(nameOpt)!,
                TimeSpan.FromSeconds(parseResult.GetValue(timeoutOpt)),
                scope.Resolve<IElevationChecker>(),
                scope.Resolve<IServiceController>(),
                Console.Out,
                Console.Error,
                ct);
        });

        // --- stop ---
        var stopCmd = new Command("stop", "Stop the FreeBird Windows service.")
        {
            nameOpt, timeoutOpt,
        };
        stopCmd.SetAction(async (parseResult, ct) =>
        {
            if (ShortCircuitNonWindows())
            {
                return 1;
            }
            var logger = BuildServiceLeafLogger();
            using var container = ServiceHostBuilder.BuildContainer(logger);
            using var scope = container.BeginLifetimeScope();
            return await StopCommand.HandleAsync(
                parseResult.GetValue(nameOpt)!,
                TimeSpan.FromSeconds(parseResult.GetValue(timeoutOpt)),
                scope.Resolve<IElevationChecker>(),
                scope.Resolve<IServiceController>(),
                Console.Out,
                Console.Error,
                ct);
        });

        // --- restart ---
        var restartCmd = new Command("restart", "Restart the FreeBird Windows service.")
        {
            nameOpt, timeoutOpt,
        };
        restartCmd.SetAction(async (parseResult, ct) =>
        {
            if (ShortCircuitNonWindows())
            {
                return 1;
            }
            var logger = BuildServiceLeafLogger();
            using var container = ServiceHostBuilder.BuildContainer(logger);
            using var scope = container.BeginLifetimeScope();
            return await RestartCommand.HandleAsync(
                parseResult.GetValue(nameOpt)!,
                TimeSpan.FromSeconds(parseResult.GetValue(timeoutOpt)),
                scope.Resolve<IElevationChecker>(),
                scope.Resolve<IServiceController>(),
                Console.Out,
                Console.Error,
                ct);
        });

        // --- status (no elevation required) ---
        var statusCmd = new Command("status", "Show the FreeBird Windows service status.")
        {
            nameOpt,
        };
        statusCmd.SetAction(parseResult =>
        {
            if (ShortCircuitNonWindows())
            {
                return 1;
            }
            var logger = BuildServiceLeafLogger();
            using var container = ServiceHostBuilder.BuildContainer(logger);
            using var scope = container.BeginLifetimeScope();
            return StatusCommand.Handle(
                parseResult.GetValue(nameOpt)!,
                scope.Resolve<IServiceController>(),
                TimeProvider.System,
                Console.Out,
                Console.Error);
        });

        // --- run (hidden SCM entrypoint) ---
        var runCmd = new Command("run", "Service Control Manager entrypoint (internal).")
        {
            configOpt,
        };
        runCmd.Hidden = true;
        runCmd.SetAction(async (parseResult, ct) =>
            await RunCommand.HandleAsync(
                ServiceConfigPath.Resolve(parseResult.GetValue(configOpt)),
                RunCommand.IsRunningAsWindowsService,
                Console.Out,
                Console.Error,
                ct));

        var serviceCommand = ServiceCommand.Create();
        serviceCommand.Subcommands.Add(initCmd);
        serviceCommand.Subcommands.Add(installCmd);
        serviceCommand.Subcommands.Add(uninstallCmd);
        serviceCommand.Subcommands.Add(startCmd);
        serviceCommand.Subcommands.Add(stopCmd);
        serviceCommand.Subcommands.Add(restartCmd);
        serviceCommand.Subcommands.Add(statusCmd);
        serviceCommand.Subcommands.Add(runCmd);
        return serviceCommand;
    }
}
