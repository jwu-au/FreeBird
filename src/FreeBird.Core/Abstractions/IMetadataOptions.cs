namespace FreeBird.Core.Abstractions;

/// <summary>
/// The subset of options the metadata-naming pipeline (resolver, tag writer) reads.
/// Implemented by both <c>ScanOptions</c> and <c>WatchOptions</c> so the resolver does
/// not need to know which CLI command invoked it.
/// </summary>
/// <remarks>
/// This is a pure shape marker — it intentionally does NOT extend <see cref="IDependency"/>
/// because it is implemented by option records, not services.
/// </remarks>
public interface IMetadataOptions
{
    /// <summary>Filename template with {artist}/{title}/{album}/{musicId} placeholders.</summary>
    string NamingTemplate { get; }

    /// <summary>When true, skip NetEase API entirely and use musicId fallback naming.</summary>
    bool Offline { get; }

    /// <summary>Per-request API timeout in seconds.</summary>
    int ApiTimeoutSeconds { get; }

    /// <summary>Cap on API calls per second. 0 = unlimited.</summary>
    int ApiRateLimit { get; }

    /// <summary>When true, write metadata tags into the decoded file.</summary>
    bool WriteTags { get; }
}
