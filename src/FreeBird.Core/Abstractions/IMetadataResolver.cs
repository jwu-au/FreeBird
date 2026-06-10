using FreeBird.Core.Metadata;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Decides how to name a decoded file based on options and (if online) NetEase API result.
/// Orchestrates: musicId extraction from filename → (optional API call) → <c>SongInfo</c>
/// construction OR <c>Fallback</c> decision with sidecar reason.
/// </summary>
public interface IMetadataResolver : IDependency
{
    /// <summary>
    /// Resolve metadata for one source .uc/.uc! file.
    /// </summary>
    /// <param name="sourcePath">Full path to original .uc/.uc! source.</param>
    /// <param name="options">
    /// Resolver behavior: Offline flag, ApiTimeoutSeconds, ApiRateLimit, etc. Both
    /// <c>ScanOptions</c> and <c>WatchOptions</c> implement this marker so the resolver
    /// does not need to know which CLI command invoked it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <c>Success(SongInfo)</c> for successful resolution; <c>Fallback(reason)</c> when
    /// offline/failed/unknown. Never throws (errors map to <c>Fallback</c>).
    /// </returns>
    Task<MetadataResolution> ResolveAsync(string sourcePath, IMetadataOptions options, CancellationToken ct);
}
