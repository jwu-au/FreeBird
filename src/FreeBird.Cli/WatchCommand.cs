using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using FreeBird.Core.Models;

namespace FreeBird.Cli;

/// <summary>
/// Builds the `fb watch` System.CommandLine subcommand and translates parsed args
/// into a <see cref="WatchOptions"/> instance. The action delegates to
/// <see cref="WatchRunner"/> to run the watch loop (T11).
/// </summary>
public static class WatchCommand
{
    public const int ExitOk = 0;
    public const int ExitFailures = 1;
    public const int ExitBadArgs = 2;

    /// <summary>
    /// Test-only hook: if non-null, the command handler invokes this delegate instead of
    /// the real <see cref="WatchRunner"/>. Tests use this to capture the parsed
    /// <see cref="WatchOptions"/> without running the watch loop.
    /// </summary>
    public static Func<WatchOptions, Task<int>>? HandlerOverride { get; set; }

    public static Command Build()
    {
        var inputArg = new Argument<string>("input-dir")
        {
            Description = "Directory to watch for new .uc / .uc! cache files.",
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
            Description = "Max parallel files being processed (1-32).",
            DefaultValueFactory = _ => 4,
        };

        var collisionOpt = new Option<CollisionPolicy>("--on-collision")
        {
            Description = "What to do when an output file already exists: skip | overwrite.",
            DefaultValueFactory = _ => CollisionPolicy.Skip,
        };

        var pollIntervalOpt = new Option<string>("--poll-interval")
        {
            Description = "How often to poll the input dir. Format: Ns or Nm (e.g. 5s, 2m). Range 1s..60m.",
            DefaultValueFactory = _ => "5s",
        };

        var stabilityChecksOpt = new Option<int>("--stability-checks")
        {
            Description = "Number of consecutive equal-size polls required before treating a file as complete (1-10).",
            DefaultValueFactory = _ => 2,
        };

        var minFileSizeOpt = new Option<long>("--min-file-size")
        {
            Description = "Skip files smaller than N bytes (default 1024).",
            DefaultValueFactory = _ => 1024L,
        };

        var skipInitialScanOpt = new Option<bool>("--skip-initial-scan")
        {
            Description = "Skip the initial pass over existing files; only process files that appear after startup.",
        };

        var logFileOpt = new Option<string?>("--log-file")
        {
            Description = "Path to a log file (default: <output>/.freebird/logs/watch-YYYY-MM-DD.log).",
        };

        var noLogFileOpt = new Option<bool>("--no-log-file")
        {
            Description = "Disable the rolling watch log file. Mutually exclusive with --log-file.",
        };

        var verboseOpt = new Option<bool>("--verbose", "-v")
        {
            Description = "Verbose logging (Debug level).",
        };

        var quietOpt = new Option<bool>("--quiet", "-q")
        {
            Description = "Quiet logging (Warning level and above only). Mutually exclusive with --verbose.",
        };

        // T19b — v3 metadata flags (identical to scan).
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

        var watchCommand = new Command(
            "watch",
            "Continuously watch <input-dir> for new .uc/.uc! files and decode them into --output.")
        {
            inputArg,
            outputOpt,
            integrityOpt,
            concurrencyOpt,
            collisionOpt,
            pollIntervalOpt,
            stabilityChecksOpt,
            minFileSizeOpt,
            skipInitialScanOpt,
            logFileOpt,
            noLogFileOpt,
            verboseOpt,
            quietOpt,
            namingTemplateOpt,
            offlineOpt,
            apiTimeoutOpt,
            apiRateLimitOpt,
            writeTagsOpt,
            noWriteTagsOpt,
            flacBinOpt,
            flacUrlOpt,
            noAutoDownloadOpt,
        };

        watchCommand.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            var integrity = parseResult.GetValue(integrityOpt);
            var concurrency = parseResult.GetValue(concurrencyOpt);
            var collision = parseResult.GetValue(collisionOpt);
            var pollRaw = parseResult.GetValue(pollIntervalOpt) ?? "5s";
            var stabilityChecks = parseResult.GetValue(stabilityChecksOpt);
            var minFileSize = parseResult.GetValue(minFileSizeOpt);
            var skipInitial = parseResult.GetValue(skipInitialScanOpt);
            var logFile = parseResult.GetValue(logFileOpt);
            var noLogFile = parseResult.GetValue(noLogFileOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var quiet = parseResult.GetValue(quietOpt);
            var namingTemplate = parseResult.GetValue(namingTemplateOpt) ?? MetadataDefaults.NamingTemplate;
            var offline = parseResult.GetValue(offlineOpt);
            var apiTimeout = parseResult.GetValue(apiTimeoutOpt);
            var apiRateLimit = parseResult.GetValue(apiRateLimitOpt);
            // v3.3: combine --write-tags (default true) with --no-write-tags opt-out.
            // --no-write-tags suppresses tag writing regardless of --write-tags value.
            var writeTags = parseResult.GetValue(writeTagsOpt) && !parseResult.GetValue(noWriteTagsOpt);
            // T15: flac provisioning flags routed through FlacOptionsBinder inside WatchRunner.
            var flacBin = parseResult.GetValue(flacBinOpt);
            var flacUrl = parseResult.GetValue(flacUrlOpt);
            var noAutoDownload = parseResult.GetValue(noAutoDownloadOpt);

            // T19b: validate metadata flags (template + api-timeout + api-rate-limit).
            var metaValidationExit = MetadataFlagsValidator.Validate(
                namingTemplate, apiTimeout, apiRateLimit, Console.Error);
            if (metaValidationExit is not null)
            {
                return metaValidationExit.Value;
            }

            // Mutex: --verbose & --quiet
            if (verbose && quiet)
            {
                Console.Error.WriteLine("--verbose and --quiet are mutually exclusive; pick one.");
                return ExitBadArgs;
            }

            // Mutex: --log-file & --no-log-file
            if (logFile is not null && noLogFile)
            {
                Console.Error.WriteLine("--log-file and --no-log-file are mutually exclusive; pick one.");
                return ExitBadArgs;
            }

            // Parse poll-interval
            TimeSpan pollInterval;
            try
            {
                pollInterval = WatchOptions.ParseDuration(pollRaw);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"--poll-interval: {ex.Message}");
                return ExitBadArgs;
            }

            // Validate poll-interval range: 1s..60m
            if (pollInterval < TimeSpan.FromSeconds(1) || pollInterval > TimeSpan.FromMinutes(60))
            {
                Console.Error.WriteLine($"--poll-interval out of range: {pollRaw} (must be between 1s and 60m).");
                return ExitBadArgs;
            }

            // Validate concurrency range: 1..32
            if (concurrency < 1 || concurrency > 32)
            {
                Console.Error.WriteLine($"--concurrency out of range: {concurrency} (must be between 1 and 32).");
                return ExitBadArgs;
            }

            // Validate stability-checks range: 1..10
            if (stabilityChecks < 1 || stabilityChecks > 10)
            {
                Console.Error.WriteLine($"--stability-checks out of range: {stabilityChecks} (must be between 1 and 10).");
                return ExitBadArgs;
            }

            // Validate min-file-size
            if (minFileSize < 0)
            {
                Console.Error.WriteLine($"--min-file-size must be non-negative: {minFileSize}");
                return ExitBadArgs;
            }

            // Validate input exists
            if (!Directory.Exists(input))
            {
                Console.Error.WriteLine($"Input directory not found: {input}");
                return ExitBadArgs;
            }

            // Output must be creatable; we don't create it here (T11 handles).
            // Just attempt a Path.GetFullPath round-trip to catch malformed paths.
            try
            {
                Path.GetFullPath(output);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Invalid --output path: {ex.Message}");
                return ExitBadArgs;
            }

            var opts = new WatchOptions
            {
                InputDirs = new[] { input },
                OutputDir = output,
                Integrity = integrity,
                Concurrency = concurrency,
                Collision = collision,
                PollInterval = pollInterval,
                StabilityChecks = stabilityChecks,
                MinFileSize = minFileSize,
                SkipInitialScan = skipInitial,
                LogFilePath = logFile,
                NoLogFile = noLogFile,
                Verbose = verbose,
                Quiet = quiet,
                NamingTemplate = namingTemplate,
                Offline = offline,
                ApiTimeoutSeconds = apiTimeout,
                ApiRateLimit = apiRateLimit,
                WriteTags = writeTags,
                FlacBin = flacBin,
                FlacUrl = flacUrl,
                NoAutoDownload = noAutoDownload,
            };

            if (HandlerOverride is not null)
            {
                return await HandlerOverride(opts);
            }

            var runner = new WatchRunner();
            return await runner.RunAsync(opts, ct);
        });

        return watchCommand;
    }
}
