using System.Collections.Generic;
using System.Text;

namespace FreeBird.Core.Naming;

/// <summary>
/// Sanitizes a candidate filename STEM (no extension) so it's safe on Windows, macOS, and Linux.
/// Pure function. Used by MetadataAwareFileNamer (T12).
/// </summary>
public static class FilenameSanitizer
{
    public const int MaxStemLength = 200;

    private const string UnknownPlaceholder = "unknown";
    private const string ReservedSuffix = "_safe";
    private const char EllipsisChar = '…'; // U+2026

    // Full-width / underscore substitutions for chars forbidden on Windows or problematic across OSes.
    private static readonly Dictionary<char, char> Substitutions = new()
    {
        { '<',  '＜' }, // U+FF1C
        { '>',  '＞' }, // U+FF1E
        { ':',  '：' }, // U+FF1A
        { '|',  '｜' }, // U+FF5C
        { '?',  '？' }, // U+FF1F
        { '/',  '／' }, // U+FF0F
        { '\\', '＼' }, // U+FF3C
        { '"',  '_' },
        { '*',  '_' },
    };

    // Windows reserved device names — match must be on the WHOLE stem (case-insensitive).
    // "CON" matches; "connor" does NOT. Only COM1–COM9 and LPT1–LPT9 are reserved (not COM10).
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Sanitize a candidate stem. Returns a safe, normalized string suitable as a filename stem.
    /// </summary>
    /// <param name="stem">Raw stem (do not include extension).</param>
    /// <returns>Sanitized stem; never null, never empty (substitutes 'unknown' if input collapses to empty).</returns>
    public static string Sanitize(string stem)
    {
        // 1. Null/empty guard
        if (string.IsNullOrWhiteSpace(stem))
        {
            return UnknownPlaceholder;
        }

        // 2 + 3. Strip control chars and apply substitutions in a single pass.
        var sb = new StringBuilder(stem.Length);
        foreach (var c in stem)
        {
            if (c <= '\u001F' || c == '\u007F')
            {
                // strip control chars
                continue;
            }

            if (Substitutions.TryGetValue(c, out var replacement))
            {
                sb.Append(replacement);
            }
            else
            {
                sb.Append(c);
            }
        }

        // 4. Strip leading and trailing dots and spaces.
        var working = sb.ToString().Trim(' ', '.');

        // 5. Re-check empty after strip.
        if (working.Length == 0)
        {
            return UnknownPlaceholder;
        }

        // 6. Reserved Windows device name guard (case-insensitive, whole-stem match).
        if (ReservedNames.Contains(working))
        {
            working += ReservedSuffix;
        }

        // 7. Length cap: truncate to 199 chars + append ellipsis = 200 total.
        if (working.Length > MaxStemLength)
        {
            working = working.Substring(0, MaxStemLength - 1) + EllipsisChar;
        }

        // 8. FINAL: NFC normalize (guards against macOS HFS+ NFD-vs-NFC issues).
        return working.Normalize(NormalizationForm.FormC);
    }
}
