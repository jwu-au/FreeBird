using System.Collections.Generic;
using System.Linq;

namespace FreeBird.Core.Metadata.Internal;

/// <summary>
/// Maps NetEase JSON DTOs to the domain SongInfo type. Keeps wire-format
/// (System.Text.Json) concerns out of the domain layer.
/// </summary>
internal static class NetEaseDtoMapper
{
    /// <summary>
    /// Convert a NetEase song DTO to domain SongInfo. Returns null if mapping fails
    /// (caller should treat as deserialization error).
    /// </summary>
    public static SongInfo? ToSongInfo(NetEaseSongDto? dto)
    {
        if (dto is null) return null;
        if (dto.Id <= 0) return null;  // sanity

        var title = dto.Name ?? string.Empty;
        var artists = (dto.Artists ?? new List<NetEaseArtistDto>())
            .Where(a => !string.IsNullOrWhiteSpace(a?.Name))
            .Select(a => a!.Name!)
            .ToList();
        var album = string.IsNullOrWhiteSpace(dto.Album?.Name) ? null : dto.Album!.Name!;

        return new SongInfo(
            MusicId: dto.Id,
            Title: title,
            Artists: artists,
            Album: album);
    }
}
