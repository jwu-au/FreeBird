using System.Globalization;
using FreeBird.Core.Decoding;

namespace FreeBird.Core.Naming;

/// <summary>
/// Extracts the leading NetEase musicId from a `.uc` / `.uc!` filename stem.
///
/// Real NetEase Mac cache files use one of two shapes:
///   * Canonical:   "&lt;musicId&gt;.uc"                          e.g. "3367798042.uc"
///   * Composite:   "&lt;musicId&gt;-_-_&lt;bitrate&gt;-_-_&lt;hash&gt;.uc!"  e.g. "3367798042-_-_5999-_-_a3865...uc!"
///
/// This extractor handles BOTH shapes. It does NOT do I/O.
///
/// Single source of truth: <see cref="MetadataAwareFileNamer"/>,
/// <see cref="FreeBird.Core.NetEase.MetadataResolver"/>, and
/// <see cref="FreeBird.Core.Watch.FilesystemSkipDecider"/> all delegate here
/// so their notion of "what is the musicId for this source?" cannot drift.
/// </summary>
public static class MusicIdExtractor
{
    /// <summary>
    /// Try to extract the musicId from a source path. Returns <c>true</c> and populates
    /// <paramref name="musicId"/> on success; returns <c>false</c> and sets
    /// <paramref name="musicId"/> to <c>0L</c> when the stem has no leading digit run.
    /// </summary>
    public static bool TryExtract(string sourcePath, out long musicId)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            musicId = 0L;
            return false;
        }

        var stem = StemBasedFileNamer.GetStem(sourcePath);
        return TryParseStem(stem, out musicId);
    }

    /// <summary>
    /// Convenience: returns the musicId as a string (the leading-digit substring of the
    /// stem) or <c>null</c> if extraction fails. Used by
    /// <see cref="FreeBird.Core.Watch.FilesystemSkipDecider"/> which compares against
    /// existing filenames on disk.
    /// </summary>
    public static string? TryExtractAsString(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var stem = StemBasedFileNamer.GetStem(sourcePath);
        var len = LeadingDigitCount(stem);
        if (len == 0)
        {
            return null;
        }

        // Sanity: the leading run must actually parse as a long. This guards against
        // pathological inputs (e.g. 25-digit runs that would overflow long) and keeps
        // TryExtract / TryExtractAsString in lockstep.
        if (!long.TryParse(stem.AsSpan(0, len), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return null;
        }

        return stem.Substring(0, len);
    }

    private static bool TryParseStem(string stem, out long musicId)
    {
        // 1. Whole-stem first (canonical "<musicId>.uc" form).
        if (long.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            musicId = id;
            return true;
        }

        // 2. Composite stem: take leading digit run, e.g. "3367798042-_-_5999-_-_xxx".
        var len = LeadingDigitCount(stem);
        if (len > 0 && long.TryParse(stem.AsSpan(0, len), NumberStyles.None, CultureInfo.InvariantCulture, out var leading))
        {
            musicId = leading;
            return true;
        }

        musicId = 0L;
        return false;
    }

    private static int LeadingDigitCount(string stem)
    {
        int i = 0;
        while (i < stem.Length && stem[i] >= '0' && stem[i] <= '9')
        {
            i++;
        }
        return i;
    }
}
