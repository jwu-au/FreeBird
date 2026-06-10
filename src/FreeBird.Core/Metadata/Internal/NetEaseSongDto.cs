using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FreeBird.Core.Metadata.Internal;

/// <summary>
/// Top-level NetEase /api/song/detail response envelope.
/// Internal wire-format DTO; do not expose outside the metadata layer.
/// </summary>
internal sealed record NetEaseResponseDto
{
    [JsonPropertyName("songs")]
    public List<NetEaseSongDto>? Songs { get; init; }

    [JsonPropertyName("code")]
    public int Code { get; init; }
}

/// <summary>
/// One song entry inside the 'songs' array. Only deserializes fields v3 needs.
/// Unknown JSON fields are ignored by default in System.Text.Json.
/// </summary>
internal sealed record NetEaseSongDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("artists")]
    public List<NetEaseArtistDto>? Artists { get; init; }

    [JsonPropertyName("album")]
    public NetEaseAlbumDto? Album { get; init; }
}

internal sealed record NetEaseArtistDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record NetEaseAlbumDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
