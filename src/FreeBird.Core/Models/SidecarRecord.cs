namespace FreeBird.Core.Models;

/// <summary>
/// Parsed contents of a <c>.freebird-failed/*.txt</c> sidecar file.
/// Returned by <c>ISidecarReader.TryRead</c>. Immutable.
/// </summary>
/// <param name="Timestamp">When the sidecar was written (from the <c>timestamp:</c> field).</param>
/// <param name="Source">Source <c>.uc</c> path string as written when the sidecar was created.</param>
/// <param name="Format">Detected audio format from the <c>format:</c> field.</param>
/// <param name="IntegrityLevel">
/// Integrity level attempted. Null when the sidecar wrote <c>-</c> (e.g. Unknown-format quarantine).
/// </param>
/// <param name="Reason">Human-readable failure reason.</param>
/// <param name="SourceSize">
/// Source file size in bytes when the sidecar was created. Null for v1 sidecars
/// (T07 treats missing as "always retry").
/// </param>
/// <param name="SourceMtime">
/// Source file last-write time when the sidecar was created. Null for v1 sidecars.
/// </param>
/// <param name="UnknownFields">
/// Forward-compatibility bucket for unrecognized <c>key: value</c> lines. Null when none.
/// </param>
public sealed record SidecarRecord(
    DateTimeOffset Timestamp,
    string Source,
    AudioFormat Format,
    IntegrityLevel? IntegrityLevel,
    string Reason,
    long? SourceSize = null,
    DateTimeOffset? SourceMtime = null,
    IReadOnlyDictionary<string, string>? UnknownFields = null);
