using FreeBird.Core.Metadata;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Renders a filename template (e.g. <c>"{artist} - {title}"</c>) against resolved
/// song metadata. Supported tokens: <c>{artist}</c>, <c>{title}</c>, <c>{album}</c>,
/// <c>{musicId}</c>. Unknown tokens pass through literally.
///
/// Pure: no I/O, no sanitization. Sanitization is a separate concern handled by
/// <see cref="FreeBird.Core.Naming.FilenameSanitizer"/> after rendering. Extension
/// append (e.g. <c>.flac</c>) is the responsibility of <c>MetadataAwareFileNamer</c>.
/// </summary>
public interface INamingTemplateRenderer : IDependency
{
    /// <summary>
    /// Render <paramref name="template"/> by substituting known tokens against
    /// <paramref name="song"/> (or fallback values when <paramref name="song"/> is null).
    /// </summary>
    /// <param name="template">Filename template, e.g. <c>"{artist} - {title}"</c>. May contain
    /// any literal characters; unknown <c>{token}</c> patterns pass through unchanged.</param>
    /// <param name="song">Resolved metadata, or <c>null</c> when the API failed and the
    /// caller is rendering an unknown-metadata filename. When null, <c>{artist}</c>,
    /// <c>{title}</c>, <c>{album}</c> all resolve to <c>unknown-*</c> fallbacks.</param>
    /// <param name="musicId">NetEase numeric id from the source filename. Always available
    /// (even when <paramref name="song"/> is null) since the caller extracts it from the
    /// source filename before invoking the renderer.</param>
    /// <returns>The rendered string. Not sanitized for filesystem use; not extension-appended.</returns>
    string Render(string template, SongInfo? song, long musicId);
}
