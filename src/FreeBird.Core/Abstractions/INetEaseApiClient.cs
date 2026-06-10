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
    /// <param name="timeout">
    /// Per-request timeout; the client creates a linked CTS internally so a long-delayed
    /// response yields <see cref="NetEaseApiResult.Timeout"/> without throwing.
    /// </param>
    /// <param name="ct">
    /// Cancellation token. User cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </param>
    /// <returns>
    /// One of Success/NotFound/NetworkError/Timeout/DeserializationError. Never throws
    /// for transport/HTTP/JSON errors — those are returned as union cases.
    /// </returns>
    Task<NetEaseApiResult> GetSongDetailAsync(long musicId, TimeSpan timeout, CancellationToken ct);
}
