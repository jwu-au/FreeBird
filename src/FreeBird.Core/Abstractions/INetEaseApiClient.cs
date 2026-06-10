using FreeBird.Core.Metadata;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Looks up song metadata from the NetEase Cloud Music public song-detail API.
/// Single responsibility: HTTP only. Domain-shape mapping happens in
/// <see cref="IMetadataResolver"/>.
/// </summary>
public interface INetEaseApiClient : IDependency
{
    /// <summary>
    /// Fetch song detail for one musicId.
    /// </summary>
    /// <param name="musicId">NetEase numeric song id (parsed from .uc filename).</param>
    /// <param name="ct">Cancellation token. Honors caller's timeout/cancellation.</param>
    /// <returns>
    /// One of Success/NotFound/NetworkError/Timeout/DeserializationError. Never throws —
    /// errors are returned as union cases.
    /// </returns>
    Task<NetEaseApiResult> GetSongDetailAsync(long musicId, CancellationToken ct);
}
