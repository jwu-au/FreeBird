using System.Collections.Generic;
using System.Text.Json;

namespace FreeBird.Core.Metadata;

/// <summary>
/// Parses the metadata JSON embedded in a NetEase <c>.ncm</c> container into a
/// domain <see cref="NcmMetadata"/>. Wire-format (System.Text.Json) concerns are
/// kept here, out of the domain record.
/// </summary>
/// <remarks>
/// The <c>artist</c> field is an array of heterogeneous [name, id] pairs
/// (string + number), which does not map cleanly onto a typed DTO, so this uses
/// <see cref="JsonDocument"/> directly. Malformed artist entries are skipped rather
/// than throwing; only structurally-invalid JSON propagates as
/// <see cref="JsonException"/> for the decoder to wrap.
/// </remarks>
public static class NcmMetadataParser
{
    /// <summary>
    /// Parse the decrypted NCM metadata JSON. Missing/empty fields become null (or an
    /// empty list for artists). Throws <see cref="JsonException"/> on invalid JSON.
    /// </summary>
    public static NcmMetadata Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var musicName = ReadString(root, "musicName");
        var album = ReadString(root, "album");
        var format = ReadString(root, "format");
        var bitrate = ReadInt32(root, "bitrate");
        var duration = ReadInt64(root, "duration");
        var artists = ReadArtists(root);

        return new NcmMetadata(
            MusicName: musicName,
            Album: album,
            Artists: artists,
            Format: format,
            Bitrate: bitrate,
            Duration: duration);
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static int? ReadInt32(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        // ponytail: only JSON numbers are accepted; string-encoded numbers are treated
        // as absent. Upgrade path: add a TryParse fallback on JsonValueKind.String if a
        // real .ncm blob is ever seen with quoted numerics.
        return element.TryGetInt32(out var value) ? value : null;
    }

    private static long? ReadInt64(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return element.TryGetInt64(out var value) ? value : null;
    }

    private static IReadOnlyList<string> ReadArtists(JsonElement root)
    {
        var names = new List<string>();

        if (!root.TryGetProperty("artist", out var artistArray))
        {
            return names;
        }

        if (artistArray.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var pair in artistArray.EnumerateArray())
        {
            // Each entry must be a 2-element [name, id] array whose element 0 is a string.
            if (pair.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            if (pair.GetArrayLength() != 2)
            {
                continue;
            }

            var first = pair[0];
            if (first.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = first.GetString();
            if (!string.IsNullOrEmpty(name))
            {
                names.Add(name);
            }
        }

        return names;
    }
}
