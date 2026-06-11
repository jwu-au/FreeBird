using System.CommandLine;
using System.Threading.Tasks;

namespace FreeBird.Cli;

/// <summary>
/// `fb install-flac` — explicit one-shot trigger for the Windows flac auto-installer.
/// Useful in CI / sysadmin / troubleshooting workflows where you want to pre-warm the
/// binary before the first `fb scan` invocation (avoiding the first-run download race).
/// On non-Windows platforms the default DI registration is a no-op installer that
/// returns <c>NotSupported</c>; the command prints a polite hint pointing at
/// brew/apt and exits 0.
/// </summary>
public static class InstallFlacCommand
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitBadArgs = 2;

    public static Command Build()
    {
        var cmd = new Command(
            "install-flac",
            "Download and install the official flac binary (Windows only; no-op on macOS/Linux).");

        var targetOpt = new Option<string?>("--target")
        {
            Description = "Target directory for the installed binaries. Default: alongside the fb executable.",
        };

        // Hidden because end users should never need to override the pinned URL; kept
        // for parity with --flac-url on scan/watch and for sysadmin / mirror scenarios.
        var urlOpt = new Option<string?>("--url")
        {
            Description = "Override download URL (advanced).",
            Hidden = true,
        };

        var verboseOpt = new Option<bool>("--verbose", "-v")
        {
            Description = "Verbose logging (Debug level).",
        };

        var quietOpt = new Option<bool>("--quiet", "-q")
        {
            Description = "Quiet logging (Warning level and above only). Mutually exclusive with --verbose.",
        };

        cmd.Options.Add(targetOpt);
        cmd.Options.Add(urlOpt);
        cmd.Options.Add(verboseOpt);
        cmd.Options.Add(quietOpt);

        cmd.SetAction(async (parseResult, ct) =>
        {
            var target = parseResult.GetValue(targetOpt);
            var url = parseResult.GetValue(urlOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var quiet = parseResult.GetValue(quietOpt);
            return await InstallFlacRunner.RunAsync(target, url, verbose, quiet, ct).ConfigureAwait(false);
        });

        return cmd;
    }
}
