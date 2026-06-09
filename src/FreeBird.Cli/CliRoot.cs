using System.CommandLine;
using System.Threading.Tasks;
using FreeBird.Core.Models;

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
        var inputArg = new Argument<string>("input-dir")
        {
            Description = "Directory containing .uc / .uc! cache files to decode.",
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

        var scanCommand = new Command("scan",
            "Decode all .uc/.uc! files in <input-dir>, write decoded audio to --output.")
        {
            inputArg, outputOpt, integrityOpt, concurrencyOpt, collisionOpt, verboseOpt, quietOpt,
        };

        scanCommand.SetAction(async (parseResult, ct) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            var integrity = parseResult.GetValue(integrityOpt);
            var concurrency = parseResult.GetValue(concurrencyOpt);
            var collision = parseResult.GetValue(collisionOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var quiet = parseResult.GetValue(quietOpt);

            return await ScanRunner.RunAsync(input, output, integrity, concurrency, collision, verbose, quiet, ct);
        });

        var root = new RootCommand("FreeBird — NetEase Music cache decoder")
        {
            scanCommand,
            WatchCommand.Build(),
        };

        return root;
    }
}
