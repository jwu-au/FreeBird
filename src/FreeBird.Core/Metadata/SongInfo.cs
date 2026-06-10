using System.Collections.Generic;

namespace FreeBird.Core.Metadata;

/// <summary>
/// Resolved music metadata for one song. Constructed by IMetadataResolver from a
/// NetEase API response (Success) or fallback inputs. Used by IFileNamer to render
/// the final output filename and by ITagWriter to inject tags into the audio file.
/// </summary>
/// <param name="MusicId">NetEase numeric song id (extracted from .uc filename).</param>
/// <param name="Title">Song title, e.g. '终不负'. Never null but may be empty when API returns blank — the resolver substitutes 'unknown-title' before yielding to IFileNamer.</param>
/// <param name="Artists">Performer names in API order, e.g. ['Rohith Pai Kasturi','IAMPRANN','IAMPRATHEEK']. Never null; may be empty when API returns artists:[]. Joined by IFileNamer (filename = ' &amp; ', tag = '/').</param>
/// <param name="Album">Album title, optional. May be null when API returns null/missing album.</param>
public sealed record SongInfo(
    long MusicId,
    string Title,
    IReadOnlyList<string> Artists,
    string? Album = null);
