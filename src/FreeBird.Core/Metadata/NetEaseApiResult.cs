namespace FreeBird.Core.Metadata;

/// <summary>
/// Outcome of a single NetEase API song-detail lookup. Sealed type hierarchy used as
/// a discriminated union — pattern-match exhaustively in callers.
/// </summary>
/// <remarks>
/// The private parameterless constructor combined with sealed nested record variants
/// guarantees the union is closed: external assemblies cannot add new cases. This
/// invariant is enforced at compile time, not at runtime, so there is no dedicated
/// unit test for it.
/// </remarks>
public abstract record NetEaseApiResult
{
    private NetEaseApiResult() { }

    /// <summary>API returned 200 with a non-empty songs array. Metadata is available.</summary>
    /// <param name="Song">The resolved song metadata.</param>
    public sealed record Success(SongInfo Song) : NetEaseApiResult;

    /// <summary>
    /// API returned 200 but <c>songs:[]</c> — the musicId is unknown to NetEase
    /// (e.g. private/regional/deleted track).
    /// </summary>
    /// <param name="MusicId">The numeric id that was queried.</param>
    public sealed record NotFound(long MusicId) : NetEaseApiResult;

    /// <summary>
    /// Network error before any HTTP response was received (DNS failure, connection
    /// refused, TLS handshake failure, etc).
    /// </summary>
    /// <param name="Message">Human-readable diagnostic for logging.</param>
    public sealed record NetworkError(string Message) : NetEaseApiResult;

    /// <summary>HTTP request exceeded the configured timeout.</summary>
    /// <param name="Elapsed">How long the request ran before timing out.</param>
    public sealed record Timeout(TimeSpan Elapsed) : NetEaseApiResult;

    /// <summary>
    /// HTTP response received but JSON could not be deserialized (server changed
    /// shape, garbage response, etc).
    /// </summary>
    /// <param name="Message">Human-readable diagnostic for logging.</param>
    public sealed record DeserializationError(string Message) : NetEaseApiResult;

    /// <summary>
    /// Server throttled/risk-controlled the request (HTTP 429/403/5xx or body
    /// code -460/-447). <paramref name="RetryAfter"/> is the server-stated minimum
    /// wait if a Retry-After header was present (clamped to 6h), else <c>null</c>.
    /// </summary>
    /// <param name="RetryAfter">
    /// Server-stated minimum wait from a Retry-After header (clamped to 6h), or
    /// <c>null</c> when absent/unparseable.
    /// </param>
    public sealed record RateLimited(TimeSpan? RetryAfter) : NetEaseApiResult;
}
