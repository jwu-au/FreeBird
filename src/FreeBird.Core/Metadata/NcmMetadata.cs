using System.Collections.Generic;

namespace FreeBird.Core.Metadata;

/// <summary>
/// Metadata embedded inside a NetEase <c>.ncm</c> container, decoded from the file's
/// internal metadata JSON (the decryption that yields that JSON happens in a later
/// stage; this record is purely the parsed result).
/// </summary>
/// <param name="MusicName">Song title from the NCM blob, or null when absent/empty.</param>
/// <param name="Album">Album title, or null when absent/empty.</param>
/// <param name="Artists">
/// Performer names in source order. The NCM blob stores artist as an array of
/// [name, id] pairs; only the names are kept here. Never null; empty when the blob
/// has no usable artist entries. Joining (e.g. ' &amp; ') happens later in naming.
/// </param>
/// <param name="Format">Audio format string, e.g. 'flac' or 'mp3', or null when absent.</param>
/// <param name="Bitrate">Bitrate in bits/sec, or null when absent.</param>
/// <param name="Duration">Duration in milliseconds, or null when absent.</param>
public sealed record NcmMetadata(
    string? MusicName,
    string? Album,
    IReadOnlyList<string> Artists,
    string? Format,
    int? Bitrate,
    long? Duration);
