namespace FreeBird.Cli;

/// <summary>
/// Pure helper that resolves the effective flac provisioning settings from CLI flags + env vars.
/// Used by ScanRunner / WatchRunner to override the default FlacResolverOptions registered
/// in CoreModule.
/// </summary>
/// <remarks>
/// Precedence: CLI flag &gt; environment variable &gt; null/false default.
///
/// Env vars:
///   FREEBIRD_FLAC_URL          (mirrors --flac-url; lower precedence)
///   FREEBIRD_NO_AUTO_DOWNLOAD  ("1" or "true" =&gt; --no-auto-download)
///
/// --flac-bin intentionally has no env var equivalent.
/// </remarks>
public static class FlacOptionsBinder
{
    public sealed record EffectiveFlacOptions(
        string? FlacBinOverride,
        string? AutoInstallUrl,
        bool DisableAutoInstall);

    /// <summary>
    /// Resolve effective settings from raw CLI + env inputs.
    /// </summary>
    /// <param name="flacBinFlag">Value passed via --flac-bin (null if absent).</param>
    /// <param name="flacUrlFlag">Value passed via --flac-url (null if absent).</param>
    /// <param name="noAutoDownloadFlag">True if --no-auto-download passed.</param>
    /// <param name="envFlacUrl">Value of FREEBIRD_FLAC_URL env (null if unset).</param>
    /// <param name="envNoAutoDownload">Value of FREEBIRD_NO_AUTO_DOWNLOAD env (null if unset).</param>
    public static EffectiveFlacOptions Resolve(
        string? flacBinFlag,
        string? flacUrlFlag,
        bool noAutoDownloadFlag,
        string? envFlacUrl,
        string? envNoAutoDownload)
    {
        // CLI flag wins; fall back to env
        var url = !string.IsNullOrWhiteSpace(flacUrlFlag) ? flacUrlFlag
                : !string.IsNullOrWhiteSpace(envFlacUrl) ? envFlacUrl
                : null;

        // --no-auto-download is OR with env (any source saying yes wins)
        var noAuto = noAutoDownloadFlag || ParseBoolEnv(envNoAutoDownload);

        return new EffectiveFlacOptions(
            FlacBinOverride: string.IsNullOrWhiteSpace(flacBinFlag) ? null : flacBinFlag,
            AutoInstallUrl: url,
            DisableAutoInstall: noAuto);
    }

    private static bool ParseBoolEnv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return false; }
        var v = value.Trim();
        return v.Equals("1", StringComparison.Ordinal)
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
