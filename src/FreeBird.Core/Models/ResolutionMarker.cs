using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeBird.Core.Models;

/// <summary>
/// Result of a per-source resolution attempt, persisted as a JSON marker file
/// under <c>.freebird-state/resolved/&lt;musicId&gt;.json</c>. Consumed by the
/// freshness/skip decider in <c>fb watch</c> to suppress infinite re-processing
/// of already-migrated outputs.
/// </summary>
/// <remarks>
/// All 14 fields are part of the v3.0.1 schema (<see cref="Schema"/> = 1).
/// Property names are PascalCase in C# and serialized as snake_case JSON via
/// <see cref="ResolutionMarkerJson.Options"/>.
/// </remarks>
public sealed record ResolutionMarker
{
    /// <summary>Schema version. v3.0.1 launches as <c>1</c>.</summary>
    [JsonRequired]
    public required int Schema { get; init; }

    /// <summary>Freshness/identity key — the source filename stem (no extension).</summary>
    [JsonRequired]
    public required string SourceStem { get; init; }

    /// <summary>Diagnostic numeric prefix of the stem. May be empty if no leading digit run.</summary>
    [JsonRequired]
    public required string MusicId { get; init; }

    /// <summary>Absolute path to the source file at the time of the attempt. Diagnostic only.</summary>
    [JsonRequired]
    public required string SourcePath { get; init; }

    /// <summary>Source file size in bytes (freshness key).</summary>
    [JsonRequired]
    public required long SourceSize { get; init; }

    /// <summary>Source file last-write time (freshness key).</summary>
    [JsonRequired]
    public required DateTimeOffset SourceMtime { get; init; }

    /// <summary>When this resolution attempt was made.</summary>
    [JsonRequired]
    public required DateTimeOffset ResolvedAt { get; init; }

    /// <summary>Outcome of the attempt. Serialized as kebab-case string.</summary>
    [JsonRequired]
    public required MarkerStatus Status { get; init; }

    /// <summary>Final POST-sanitization output filename (with extension).</summary>
    [JsonRequired]
    public required string OutputName { get; init; }

    /// <summary>Audio format label (e.g., <c>Flac</c>, <c>Mp3</c>, <c>M4a</c>).</summary>
    [JsonRequired]
    public required string Format { get; init; }

    /// <summary>Integrity level label (e.g., <c>Off</c>, <c>L1</c>, <c>L3</c>, <c>Auto</c>).</summary>
    [JsonRequired]
    public required string Integrity { get; init; }

    /// <summary>The naming template literal that produced <see cref="OutputName"/>.</summary>
    [JsonRequired]
    public required string NamingTemplate { get; init; }

    /// <summary>
    /// Failure reason. <c>null</c> when <see cref="Status"/> is <see cref="MarkerStatus.Resolved"/>;
    /// otherwise mirrors the status (e.g., <c>"metadata-empty"</c>). Serialized as explicit <c>null</c>.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Earliest time at which a re-attempt is allowed. <c>null</c> when
    /// <see cref="Status"/> is <see cref="MarkerStatus.Resolved"/>. Serialized as explicit <c>null</c>.
    /// </summary>
    public DateTimeOffset? RetryAfter { get; init; }

    /// <summary>
    /// Outcome of the post-rename tag-write step (v3.0.1 T04 additive field).
    /// One of <c>"ok"</c>, <c>"skipped"</c>, <c>"failed"</c>, or <c>null</c>.
    /// <c>null</c> when <c>WriteTags</c> was false (the default).
    /// Forward-compatible: pre-v3.0.1 markers omit this field and still parse.
    /// </summary>
    [JsonPropertyName("tag_write_status")]
    public string? TagWriteStatus { get; init; }

    /// <summary>
    /// Reason code when <see cref="TagWriteStatus"/> is <c>"failed"</c>
    /// (e.g. <c>"tag-tool-missing"</c>, <c>"tag-write-failed"</c>). Otherwise <c>null</c>.
    /// Forward-compatible: pre-v3.0.1 markers omit this field and still parse.
    /// </summary>
    [JsonPropertyName("tag_write_reason")]
    public string? TagWriteReason { get; init; }
}

/// <summary>
/// Outcome of a single per-source resolution attempt.
/// Serialized via <see cref="MarkerStatusJsonConverter"/> as kebab-case strings.
/// </summary>
public enum MarkerStatus
{
    /// <summary>The source was successfully resolved and written.</summary>
    Resolved,

    /// <summary>Metadata fetch succeeded but returned an empty payload.</summary>
    MetadataEmpty,

    /// <summary>Metadata fetch failed (network/HTTP/timeout).</summary>
    MetadataFetchFailed,

    /// <summary>Metadata fetch returned a payload that could not be deserialized.</summary>
    MetadataDeserializeFailed,
}

/// <summary>
/// Custom converter for <see cref="MarkerStatus"/> using the canonical kebab-case
/// wire format. The built-in <c>JsonStringEnumConverter</c> with snake_case policy
/// would emit underscores (<c>metadata_empty</c>), not hyphens; we want hyphens
/// for human readability and consistency with the rest of the FreeBird wire format.
/// </summary>
internal sealed class MarkerStatusJsonConverter : JsonConverter<MarkerStatus>
{
    public override MarkerStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value switch
        {
            "resolved" => MarkerStatus.Resolved,
            "metadata-empty" => MarkerStatus.MetadataEmpty,
            "metadata-fetch-failed" => MarkerStatus.MetadataFetchFailed,
            "metadata-deserialize-failed" => MarkerStatus.MetadataDeserializeFailed,
            _ => throw new JsonException($"Unknown MarkerStatus value: '{value}'."),
        };
    }

    public override void Write(Utf8JsonWriter writer, MarkerStatus value, JsonSerializerOptions options)
    {
        var s = value switch
        {
            MarkerStatus.Resolved => "resolved",
            MarkerStatus.MetadataEmpty => "metadata-empty",
            MarkerStatus.MetadataFetchFailed => "metadata-fetch-failed",
            MarkerStatus.MetadataDeserializeFailed => "metadata-deserialize-failed",
            _ => throw new JsonException($"Unknown MarkerStatus enum value: {value}."),
        };
        writer.WriteStringValue(s);
    }
}

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for <see cref="ResolutionMarker"/>
/// serialization. Uses snake_case property names, kebab-case enum strings,
/// indented output, and explicit (non-omitted) <c>null</c> values for nullable fields.
/// </summary>
public static class ResolutionMarkerJson
{
    /// <summary>The canonical serializer options for marker files.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Converters = { new MarkerStatusJsonConverter() },
    };
}

/// <summary>
/// Per-<see cref="MarkerStatus"/> retry backoff table (D4 in the design spec).
/// </summary>
public static class ResolutionMarkerRetry
{
    /// <summary>
    /// Returns the minimum delay before a re-attempt is permitted for the given
    /// status, or <c>null</c> when the status is <see cref="MarkerStatus.Resolved"/>
    /// (no retry needed).
    /// </summary>
    public static TimeSpan? For(MarkerStatus status) => status switch
    {
        MarkerStatus.Resolved => null,
        MarkerStatus.MetadataFetchFailed => TimeSpan.FromHours(1),
        MarkerStatus.MetadataEmpty => TimeSpan.FromDays(7),
        MarkerStatus.MetadataDeserializeFailed => TimeSpan.FromHours(24),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown MarkerStatus."),
    };
}
