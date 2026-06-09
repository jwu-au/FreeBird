using System.Globalization;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Sidecar;

/// <summary>
/// Reads sidecar <c>.txt</c> files written by <c>FileProcessor</c>.
/// Tolerant of v1 (5-field) and v2 (7-field) formats.
/// Returns null for missing, unreadable, or fundamentally malformed sidecars.
/// </summary>
public sealed class TextSidecarReader : ISidecarReader
{
    public async Task<SidecarRecord?> TryReadAsync(string sidecarPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        DateTimeOffset? timestamp = null;
        string? source = null;
        AudioFormat? format = null;
        IntegrityLevel? integrityLevel = null;
        string? reason = null;
        long? sourceSize = null;
        DateTimeOffset? sourceMtime = null;
        Dictionary<string, string>? unknown = null;

        foreach (var rawLine in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var colonIdx = rawLine.IndexOf(':');
            if (colonIdx <= 0)
            {
                continue;
            }

            var key = rawLine.Substring(0, colonIdx).Trim().ToLowerInvariant();
            var value = rawLine.Substring(colonIdx + 1).TrimStart().TrimEnd('\r', '\n', ' ', '\t');
            if (key.Length == 0)
            {
                continue;
            }

            switch (key)
            {
                case "timestamp":
                    if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                    {
                        timestamp = ts;
                    }
                    break;
                case "source":
                    source = value;
                    break;
                case "format":
                    if (Enum.TryParse<AudioFormat>(value, ignoreCase: true, out var fmt) && Enum.IsDefined(typeof(AudioFormat), fmt))
                    {
                        format = fmt;
                    }
                    else
                    {
                        return null;
                    }
                    break;
                case "integrity":
                    if (value != "-")
                    {
                        if (Enum.TryParse<IntegrityLevel>(value, ignoreCase: true, out var lvl) && Enum.IsDefined(typeof(IntegrityLevel), lvl))
                        {
                            integrityLevel = lvl;
                        }
                    }
                    break;
                case "reason":
                    reason = value;
                    break;
                case "source_size":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sz) && sz >= 0)
                    {
                        sourceSize = sz;
                    }
                    break;
                case "source_mtime":
                    if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var mt))
                    {
                        sourceMtime = mt;
                    }
                    break;
                default:
                    (unknown ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[key] = value;
                    break;
            }
        }

        if (timestamp is null || source is null || format is null || reason is null)
        {
            return null;
        }

        return new SidecarRecord(
            Timestamp: timestamp.Value,
            Source: source,
            Format: format.Value,
            IntegrityLevel: integrityLevel,
            Reason: reason,
            SourceSize: sourceSize,
            SourceMtime: sourceMtime,
            UnknownFields: unknown);
    }
}
