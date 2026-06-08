using System;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Decoding;

/// <summary>
/// Default naming strategy: strip .uc/.uc! suffix from source basename, append detected format extension.
/// </summary>
public sealed class StemPlusExtensionNamingStrategy : INamingStrategy
{
    public string GetOutputFileName(string sourceFilePath, AudioFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        if (format == AudioFormat.Unknown)
        {
            throw new ArgumentException("Cannot generate filename for unknown format", nameof(format));
        }

        // Strip directory + .uc/.uc! suffix via shared helper
        var stem = GetStem(sourceFilePath);

        return stem + GetExtension(format);
    }

    /// <summary>
    /// Extract the stem (filename without .uc / .uc! suffix and without directory) from a source path.
    /// Public so other components (e.g. FileProcessor quarantine) can use the same logic.
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
        // GetOutputFileName(nothing.txt, mp3) still produces "nothing.txt.mp3".
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
