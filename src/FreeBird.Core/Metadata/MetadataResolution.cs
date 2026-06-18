namespace FreeBird.Core.Metadata;

/// <summary>
/// Outcome of metadata resolution for a single song. Returned by <c>IMetadataResolver</c>
/// to the file-namer pipeline. Sealed type hierarchy used as a discriminated union —
/// pattern-match exhaustively in callers.
/// </summary>
/// <remarks>
/// The private parameterless constructor combined with sealed nested record variants
/// guarantees the union is closed: external assemblies cannot add new cases. This
/// invariant is enforced at compile time, not at runtime, so there is no dedicated
/// unit test for it.
/// </remarks>
public abstract record MetadataResolution
{
    private MetadataResolution() { }

    /// <summary>
    /// Metadata was successfully resolved (API hit or future cache). Use <paramref name="Song"/>
    /// to render the final filename and (optionally) write tags.
    /// </summary>
    /// <param name="Song">The resolved song metadata.</param>
    public sealed record Success(SongInfo Song) : MetadataResolution;

    /// <summary>
    /// Metadata could not be resolved. File is decoded but named using musicId fallback.
    /// <paramref name="SidecarReason"/> becomes the <c>reason</c> field in the
    /// <c>.freebird-failed</c> sidecar entry.
    /// </summary>
    /// <param name="SidecarReason">Short machine-readable reason code (e.g. "api-not-found", "offline-mode", "api-timeout").</param>
    public sealed record Fallback(string SidecarReason) : MetadataResolution
    {
        /// <summary>
        /// Server-stated minimum wait (from a rate-limit Retry-After header, already
        /// clamped at the parse site). <c>null</c> unless this is a rate-limited
        /// fallback that carried the header. Additive init-only member so existing
        /// <c>new Fallback("...")</c> call sites keep compiling unchanged.
        /// </summary>
        public TimeSpan? ServerRetryAfter { get; init; }
    }
}
