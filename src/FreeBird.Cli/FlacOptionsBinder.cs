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
    /// <param name="defaultUrl">
    /// Platform-appropriate fallback download URL used when neither <paramref name="flacUrlFlag"/>
    /// nor <paramref name="envFlacUrl"/> is set. Callers pass the pinned Xiph ZIP URL on Windows
    /// (so <c>scan</c>/<c>watch</c> trigger the auto-installer like <c>install-flac</c> does) and
    /// <c>null</c> on macOS/Linux (where auto-install is a NoOp and the user installs flac via
    /// their package manager). Keeping it a parameter leaves this helper platform-agnostic.
    /// </param>
    public static EffectiveFlacOptions Resolve(
        string? flacBinFlag,
        string? flacUrlFlag,
        bool noAutoDownloadFlag,
        string? envFlacUrl,
        string? envNoAutoDownload,
        string? defaultUrl = null)
    {
        // CLI flag wins; then env; then the platform default supplied by the caller.
        var url = !string.IsNullOrWhiteSpace(flacUrlFlag) ? flacUrlFlag
                : !string.IsNullOrWhiteSpace(envFlacUrl) ? envFlacUrl
                : !string.IsNullOrWhiteSpace(defaultUrl) ? defaultUrl
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
