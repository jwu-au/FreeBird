using System;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;

namespace FreeBird.Core.Decoding;

/// <summary>
/// v1/v2 file namer: strip .uc/.uc! suffix from source basename, append detected format extension.
/// Ignores the <c>metadata</c> parameter — v1/v2 stem-based behavior is preserved exactly.
///
/// T13 (v3) note: this type is no longer DI-bound as <see cref="IFileNamer"/> — the v3 default
/// is <see cref="FreeBird.Core.Naming.MetadataAwareFileNamer"/>. StemBasedFileNamer is retained
/// because its <see cref="GetStem"/> helper is still used by other components (quarantine,
/// FilesystemSkipDecider) and because legacy E2E tests instantiate it directly when they
/// need stem-based output filenames (see <c>StemNamerTestOverride</c>).
/// </summary>
public sealed class StemBasedFileNamer : IFileNamer
{
    // namingTemplate is intentionally ignored: stem-based naming derives the
    // filename solely from the source file's stem (v1/v2 behavior) and has
    // no template to render. Accepted only to satisfy the IFileNamer contract.
    public string GetTargetName(string sourcePath, AudioFormat format, SongInfo? metadata, string? namingTemplate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (format == AudioFormat.Unknown)
        {
            throw new ArgumentException("Cannot generate filename for unknown format", nameof(format));
        }

        // Strip directory + .uc/.uc! suffix via shared helper
        var stem = GetStem(sourcePath);

        return stem + GetExtension(format);
    }

    /// <summary>
    /// Extract the stem (filename without .uc / .uc! suffix and without directory) from a source path.
    /// Public so other components (e.g. FileProcessor quarantine, FilesystemSkipDecider) can use the same logic.
    /// For inputs without a .uc/.uc! suffix, the filename is returned unchanged (preserving any other extension).
    /// </summary>
    public static string GetStem(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        // Robustly extract filename across OS path separators (input may originate from a different OS)
        var slashIdx = sourcePath.LastIndexOfAny(new[] { '/', '\\' });
        var filename = slashIdx >= 0 ? sourcePath[(slashIdx + 1)..] : sourcePath;

        // Strip .uc! (4 chars) first, then .uc (3 chars), both case-insensitive.
        // Non-uc filenames are returned as-is (preserving any other extension), so
        // GetTargetName(nothing.txt, mp3, null) still produces "nothing.txt.mp3".
        if (filename.EndsWith(".uc!", StringComparison.OrdinalIgnoreCase))
        {
            return filename[..^4];
        }
        if (filename.EndsWith(".uc", StringComparison.OrdinalIgnoreCase))
        {
            return filename[..^3];
        }
        return filename;
    }

    private static string GetExtension(AudioFormat format) => format switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Flac => ".flac",
        AudioFormat.M4a => ".m4a",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported format"),
    };
}
