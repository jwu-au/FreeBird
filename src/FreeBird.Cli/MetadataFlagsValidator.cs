using System.IO;

namespace FreeBird.Cli;

/// <summary>
/// Shared validation for the five v3 metadata CLI flags exposed by both
/// <c>fb scan</c> and <c>fb watch</c>: <c>--naming-template</c>,
/// <c>--api-timeout</c>, <c>--api-rate-limit</c>. Returns <c>null</c> on success
/// or a CLI exit code (2 = bad args) and writes a helpful message to <paramref name="err"/>
/// on failure.
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

        if (apiTimeoutSeconds < 1 || apiTimeoutSeconds > 300)
        {
            err.WriteLine($"--api-timeout out of range: {apiTimeoutSeconds} (must be between 1 and 300 seconds).");
            return ExitBadArgs;
        }

        if (apiRateLimit < 0 || apiRateLimit > 100)
        {
            err.WriteLine($"--api-rate-limit out of range: {apiRateLimit} (must be between 0 and 100; 0 = unlimited).");
            return ExitBadArgs;
        }

        return null;
    }
}
