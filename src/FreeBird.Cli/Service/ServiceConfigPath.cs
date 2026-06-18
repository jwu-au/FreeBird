using System;
using System.IO;

namespace FreeBird.Cli.Service;

/// <summary>
/// Resolves the on-disk path of the service config JSON for every <c>fb service</c>
/// consumer (install / start / stop / restart / status / run, and the option default
/// wired in <c>CliRoot</c>).
///
/// <para><b>Why this exists.</b> <see cref="FreeBird.Core.Service.ConfigDefaults.DefaultConfigPath"/>
/// is the <i>literal</i> token <c>%ProgramData%\FreeBird\config.json</c> (Core deliberately
/// stores the unexpanded token because expansion is a host concern). In v3.5.0 the
/// <c>--config</c> option default handed that raw token straight to the file-existence
/// check, the SCM <c>BinaryPathName</c>, and the <c>run</c> loader — so
/// <c>fb service install</c> failed with
/// "Config file not found: %ProgramData%\FreeBird\config.json". Only <c>fb service init</c>
/// worked because it expanded the default itself. Funnelling all consumers through this
/// helper fixes the whole chain and also lets operators use tokens like
/// <c>%USERPROFILE%</c> in <c>--config</c>.</para>
///
/// <para>The helper is pure and OS-agnostic: it uses
/// <see cref="Environment.ExpandEnvironmentVariables"/> (a no-op on token-free paths,
/// and a no-op for unknown tokens) and <see cref="Environment.SpecialFolder.CommonApplicationData"/>
/// for the default, mirroring <c>LogPathResolver.BuildProgramDataDefault</c> and
/// <c>InitCommand.BuildDefaultConfigPath</c>.</para>
/// </summary>
public static class ServiceConfigPath
{
    /// <summary>
    /// Resolves <paramref name="requestedPath"/> to a real filesystem path.
    /// A null/empty/whitespace request returns the expanded
    /// <c>&lt;CommonApplicationData&gt;/FreeBird/config.json</c> default; any supplied
    /// path has its environment-variable tokens expanded.
    /// </summary>
    public static string Resolve(string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return BuildProgramDataDefault();
        }

        return Environment.ExpandEnvironmentVariables(requestedPath);
    }

    private static string BuildProgramDataDefault()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "FreeBird", "config.json");
    }
}
