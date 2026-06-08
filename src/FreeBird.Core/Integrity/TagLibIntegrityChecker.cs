using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Integrity;

/// <summary>
/// L1 (structural) integrity checker backed by TagLib#.
/// Opens the file and verifies duration > 0. Returns Failed on parse errors,
/// throws on infrastructural errors (file not found, access denied).
/// </summary>
public sealed class TagLibIntegrityChecker : IL1IntegrityChecker
{
    public Task<IntegrityResult> CheckAsync(
        string filePath,
        AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Audio file not found", filePath);
        }

        return Task.Run(() => CheckSync(filePath), cancellationToken);
    }

    private static IntegrityResult CheckSync(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var duration = file.Properties?.Duration ?? TimeSpan.Zero;
            if (duration <= TimeSpan.Zero)
            {
                return IntegrityResult.Failed(IntegrityLevel.L1, "Duration is zero or unreadable");
            }
            return IntegrityResult.Passed(IntegrityLevel.L1);
        }
        catch (TagLib.CorruptFileException ex)
        {
            return IntegrityResult.Failed(IntegrityLevel.L1, $"Corrupt file: {ex.Message}");
        }
        catch (TagLib.UnsupportedFormatException ex)
        {
            return IntegrityResult.Failed(IntegrityLevel.L1, $"Unsupported format: {ex.Message}");
        }
    }
}
