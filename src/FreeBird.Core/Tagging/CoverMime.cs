using System;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Detects an image MIME type from its leading magic bytes.
/// </summary>
/// <remarks>
/// ponytail: only JPEG and PNG are recognised. NetEase cover art is always one of
/// these two; anything else falls back to <c>image/jpeg</c> (the more common case).
/// Upgrade path if a third format ever appears: add another magic-byte branch here.
/// </remarks>
internal static class CoverMime
{
    /// <summary>JPEG starts with FF D8 FF; PNG with 89 50 4E 47. Default image/jpeg.</summary>
    public static string Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        {
            return "image/png";
        }

        return "image/jpeg";
    }
}
