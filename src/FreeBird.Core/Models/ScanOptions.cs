namespace FreeBird.Core.Models;

/// <summary>
/// Configuration for a scan run, derived from CLI flags.
/// </summary>
public sealed record ScanOptions(
    string InputDirectory,
    string OutputDirectory,
    IntegrityLevel Integrity = IntegrityLevel.Auto,
    int Concurrency = 2,
    CollisionPolicy OnCollision = CollisionPolicy.Skip)
{
    /// <summary>
    /// Filename template using <c>{artist}</c>, <c>{title}</c>, <c>{album}</c>, and
    /// <c>{musicId}</c> placeholders. Default: <c>"{artist} - {title}"</c>.
    /// </summary>
    public string NamingTemplate { get; init; } = "{artist} - {title}";

    /// <summary>
    /// Skip NetEase API entirely; always use musicId fallback naming. Maps to the
    /// <c>--offline</c> CLI flag. Default: <c>false</c>.
    /// </summary>
    public bool Offline { get; init; } = false;

    /// <summary>
    /// API request timeout in seconds. Maps to <c>--api-timeout</c>. Default: 10.
    /// </summary>
    public int ApiTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Cap on API calls per second. <c>0</c> = unlimited. Maps to
    /// <c>--api-rate-limit</c>. Default: 0.
    /// </summary>
    public int ApiRateLimit { get; init; } = 0;

    /// <summary>
    /// Write metadata tags into the decoded file (ID3/Vorbis/MP4). Maps to
    /// <c>--write-tags</c>. Default: <c>false</c>.
    /// </summary>
    public bool WriteTags { get; init; } = false;
}

/// <summary>
/// Behavior when the target output filename already exists from a different source.
/// </summary>
public enum CollisionPolicy
{
    Skip = 0,
    Overwrite = 1,
}
