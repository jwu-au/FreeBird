using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Decoding;

/// <summary>
/// Detects audio format by inspecting magic bytes in the first 12 bytes of a file.
/// Returns Unknown if no known signature matches or file is too short.
/// </summary>
public sealed class MagicByteFormatSniffer : IFormatSniffer
{
    private const int PeekBytes = 12;

    public async Task<AudioFormat> SniffAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        var buffer = new byte[PeekBytes];
        int totalRead = 0;
        while (totalRead < PeekBytes)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, PeekBytes - totalRead),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            totalRead += read;
        }

        return SniffBytes(buffer.AsSpan(0, totalRead));
    }

    /// <summary>
    /// Pure detection over a byte span. Public for direct testing.
    /// </summary>
    public static AudioFormat SniffBytes(ReadOnlySpan<byte> bytes)
    {
        // FLAC: "fLaC" at offset 0 (4 bytes)
        if (bytes.Length >= 4 &&
            bytes[0] == 0x66 && bytes[1] == 0x4C && bytes[2] == 0x61 && bytes[3] == 0x43)
        {
            return AudioFormat.Flac;
        }

        // MP3 with ID3v2 tag: "ID3" at offset 0
        if (bytes.Length >= 3 &&
            bytes[0] == 0x49 && bytes[1] == 0x44 && bytes[2] == 0x33)
        {
            return AudioFormat.Mp3;
        }

        // M4A: "ftyp" at offset 4
        if (bytes.Length >= 8 &&
            bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70)
        {
            return AudioFormat.M4a;
        }

        // MP3 raw frame: 0xFF + (byte2 & 0xE0 == 0xE0)
        if (bytes.Length >= 2 &&
            bytes[0] == 0xFF && (bytes[1] & 0xE0) == 0xE0)
        {
            return AudioFormat.Mp3;
        }

        return AudioFormat.Unknown;
    }
}
