using System.IO;
using FreeBird.Core.Models;

namespace FreeBird.Cli;

/// <summary>
/// Validates the v3 metadata CLI flags that have constraints beyond shape parsing:
/// <c>--naming-template</c> (non-empty, must contain at least one recognised placeholder),
/// <c>--api-timeout</c> (1–300 seconds), and <c>--api-rate-limit</c> (0–100 req/sec; 0 = unlimited).
/// The two remaining v3 flags (<c>--offline</c> and <c>--write-tags</c>) are plain boolean
/// switches and need no validation beyond System.CommandLine's parser.
///
/// Returns <c>null</c> on success or a CLI exit code (2 = bad args), writing a helpful
/// message to <paramref name="err"/> on failure.
/// </summary>
internal static class MetadataFlagsValidator
{
    public const int ExitBadArgs = 2;

    private static readonly string[] KnownPlaceholders =
    {
        "{artist}", "{title}", "{album}", "{musicId}",
    };

    public static int? Validate(string namingTemplate, int apiTimeoutSeconds, int apiRateLimit, TextWriter err)
    {
        if (string.IsNullOrWhiteSpace(namingTemplate))
        {
            err.WriteLine("--naming-template must not be empty; use {artist}, {title}, {album}, or {musicId}.");
            return ExitBadArgs;
        }

        var hasPlaceholder = false;
        foreach (var placeholder in KnownPlaceholders)
        {
            if (namingTemplate.Contains(placeholder))
            {
                hasPlaceholder = true;
                break;
            }
        }
        if (!hasPlaceholder)
        {
            err.WriteLine(
                $"--naming-template must contain at least one of {{artist}}, {{title}}, {{album}}, {{musicId}} (got: \"{namingTemplate}\").");
            return ExitBadArgs;
        }

        if (apiTimeoutSeconds < MetadataDefaults.ApiTimeoutMin || apiTimeoutSeconds > MetadataDefaults.ApiTimeoutMax)
        {
            err.WriteLine(
                $"--api-timeout out of range: {apiTimeoutSeconds} (must be between {MetadataDefaults.ApiTimeoutMin} and {MetadataDefaults.ApiTimeoutMax} seconds).");
            return ExitBadArgs;
        }

        if (apiRateLimit < MetadataDefaults.ApiRateLimitMin || apiRateLimit > MetadataDefaults.ApiRateLimitMax)
        {
            err.WriteLine(
                $"--api-rate-limit out of range: {apiRateLimit} (must be between {MetadataDefaults.ApiRateLimitMin} and {MetadataDefaults.ApiRateLimitMax}; 0 = unlimited).");
            return ExitBadArgs;
        }

        return null;
    }
}
