using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Naming;

/// <summary>
/// Default <see cref="INamingTemplateRenderer"/>. Substitutes <c>{artist}</c>,
/// <c>{title}</c>, <c>{album}</c>, <c>{musicId}</c> in a template string via a
/// single-pass regex scan. Unknown tokens are left untouched.
///
/// Pure: no I/O. Output is unsanitized — callers apply
/// <see cref="FilenameSanitizer"/> before writing to disk.
/// </summary>
public sealed class NamingTemplateRenderer : INamingTemplateRenderer
{
    /// <summary>Default template used when the user does not override.</summary>
    public const string DefaultTemplate = "{artist} - {title}";

    /// <summary>Separator joining multiple artists in the FILENAME context.
    /// (The tag-writing separator is different; see <c>ITagWriter</c>.)</summary>
    public const string FilenameArtistSeparator = " & ";

    /// <summary>Fallback when the artists list is empty or all-blank.</summary>
    public const string UnknownArtist = "unknown-artist";

    /// <summary>Fallback when the title is null/empty/whitespace.</summary>
    public const string UnknownTitle = "unknown-title";

    /// <summary>Fallback when the album is null/empty/whitespace.</summary>
    public const string UnknownAlbum = "unknown-album";

    // Match one {token} per scan. Identifier = ASCII letters only (lowercase per
    // spec, with the camelCase 'musicId' exception also being ASCII letters).
    // Single-pass replace prevents recursive expansion of substituted values.
    private static readonly Regex TokenRegex = new(@"\{[a-zA-Z]+\}", RegexOptions.Compiled);

    /// <inheritdoc />
    public string Render(string template, SongInfo? song, long musicId)
    {
        // Pre-compute substitution values once; the regex callback uses these.
        string artist = ComputeArtist(song);
        string title = ComputeTitle(song);
        string album = ComputeAlbum(song);
        string musicIdStr = musicId.ToString(CultureInfo.InvariantCulture);

        return TokenRegex.Replace(template, match => match.Value switch
        {
            "{artist}" => artist,
            "{title}" => title,
            "{album}" => album,
            "{musicId}" => musicIdStr,
            _ => match.Value,   // unknown token — pass through literally
        });
    }

    private static string ComputeArtist(SongInfo? song)
    {
        if (song is null || song.Artists.Count == 0)
        {
            return UnknownArtist;
        }

        // Filter out blank entries; if nothing remains, fall back.
        var nonBlank = song.Artists.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        if (nonBlank.Count == 0)
        {
            return UnknownArtist;
        }

        return string.Join(FilenameArtistSeparator, nonBlank);
    }

    private static string ComputeTitle(SongInfo? song)
    {
        if (song is null || string.IsNullOrWhiteSpace(song.Title))
        {
            return UnknownTitle;
        }

        return song.Title;
    }

    private static string ComputeAlbum(SongInfo? song)
    {
        if (song is null || string.IsNullOrWhiteSpace(song.Album))
        {
            return UnknownAlbum;
        }

        return song.Album;
    }
}
