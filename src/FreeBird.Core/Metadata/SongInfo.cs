using System.Collections.Generic;

namespace FreeBird.Core.Metadata;

/// <summary>
/// Resolved music metadata for a single song. Placeholder shape; full fields added in T02.
/// </summary>
public sealed record SongInfo(
    long MusicId,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album = null);
