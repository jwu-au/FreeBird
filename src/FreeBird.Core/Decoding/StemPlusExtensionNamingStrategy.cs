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

        // Strip directory components — handle both '/' and '\' regardless of host OS,
        // since the input may originate from a different platform than the runtime.
        var lastSeparator = sourceFilePath.LastIndexOfAny(new[] { '/', '\\' });
        var basename = lastSeparator >= 0
            ? sourceFilePath[(lastSeparator + 1)..]
            : sourceFilePath;

        // Strip .uc or .uc! (case-insensitive)
        var stem = StripUcSuffix(basename);

        return stem + GetExtension(format);
    }

    private static string StripUcSuffix(string basename)
    {
        if (basename.EndsWith(".uc!", StringComparison.OrdinalIgnoreCase))
        {
            return basename[..^4];
        }
        if (basename.EndsWith(".uc", StringComparison.OrdinalIgnoreCase))
        {
            return basename[..^3];
        }
        return basename;
    }

    private static string GetExtension(AudioFormat format) => format switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Flac => ".flac",
        AudioFormat.M4a => ".m4a",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported format"),
    };
}
