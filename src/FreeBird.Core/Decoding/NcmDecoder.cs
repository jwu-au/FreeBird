using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;

namespace FreeBird.Core.Decoding;

/// <summary>
/// Streaming decoder for NetEase Cloud Music <c>.ncm</c> files. Validates the magic header,
/// AES-decrypts the embedded RC4 key and metadata, extracts cover art, and RC4-XOR decrypts the
/// audio body straight to the caller-supplied output stream (0x8000 chunks; the body is never
/// buffered whole). This is the byte-exact inverse of the test encoder / reference NeteaseCrypt.
/// Does not dispose the output stream — the caller owns its lifetime.
/// </summary>
public sealed class NcmDecoder : INcmDecoder
{
    private static readonly byte[] CoreKey = "hzHRAmso5kInbaxW"u8.ToArray();
    private static readonly byte[] ModifyKey = "#14ljk_!\\]&0U<'("u8.ToArray();

    private const int Magic1 = 0x4e455443;
    private const int Magic2 = 0x4d414446;
    private const int BodyChunkSize = 0x8000;

    // The decrypted RC4-key block is prefixed with 17 bytes ("neteasecloudmusic") the decoder drops.
    private const int Rc4KeyDropPrefix = 17;

    // The XOR'd metadata blob is prefixed with this 22-byte ASCII marker before the base64 payload.
    private const int MetadataMarkerLength = 22; // "163 key(Don't modify):"

    // The AES-decrypted metadata payload is prefixed with "music:" (6 bytes) before the JSON.
    private const int MusicPrefixLength = 6;

    public async Task<NcmDecodeResult> DecodeAsync(string sourcePath, Stream output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(output);

        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BodyChunkSize, useAsync: true);

        // --- 1. Magic ---
        if (ReadInt32(source) != Magic1 || ReadInt32(source) != Magic2)
        {
            throw new NcmDecodeException("not an NCM file: bad magic");
        }
        Skip(source, 2);

        try
        {
            // --- 2. RC4 key -> keybox ---
            byte[] keyBox = ReadKeyBox(source);

            // --- 3. Metadata (may be absent) ---
            NcmMetadata? metadata = ReadMetadata(source);

            // --- 4. Skip CRC32 (4) + image version (1) ---
            Skip(source, 5);

            // --- 5. Cover ---
            byte[]? cover = ReadCover(source);

            // --- 6. Body (streamed) + 7. provisional format sniff ---
            AudioFormat format = await DecryptBodyAsync(source, output, keyBox, ct).ConfigureAwait(false);

            return new NcmDecodeResult(metadata, cover, format);
        }
        catch (EndOfStreamException ex)
        {
            throw new NcmDecodeException("truncated NCM file", ex);
        }
        catch (CryptographicException ex)
        {
            throw new NcmDecodeException("NCM decryption failed", ex);
        }
    }

    private static byte[] ReadKeyBox(Stream source)
    {
        int keyLength = ReadInt32(source);
        if (keyLength <= 0)
        {
            throw new NcmDecodeException("corrupt NCM file: bad key length");
        }

        byte[] keyData = ReadExact(source, keyLength);
        for (int i = 0; i < keyData.Length; i++)
        {
            keyData[i] ^= 0x64;
        }

        byte[] decrypted = AesEcbDecrypt(CoreKey, keyData);
        if (decrypted.Length <= Rc4KeyDropPrefix)
        {
            throw new NcmDecodeException("corrupt NCM file: short key");
        }

        byte[] rc4Key = decrypted[Rc4KeyDropPrefix..];
        return BuildKeyBox(rc4Key);
    }

    private static NcmMetadata? ReadMetadata(Stream source)
    {
        int metadataLength = ReadInt32(source);
        if (metadataLength == 0)
        {
            return null;
        }
        if (metadataLength < 0)
        {
            throw new NcmDecodeException("corrupt NCM metadata");
        }

        byte[] block = ReadExact(source, metadataLength);
        for (int i = 0; i < block.Length; i++)
        {
            block[i] ^= 0x63;
        }

        try
        {
            string base64 = Encoding.ASCII.GetString(block, MetadataMarkerLength, block.Length - MetadataMarkerLength);
            byte[] cipher = Convert.FromBase64String(base64);
            byte[] plain = AesEcbDecrypt(ModifyKey, cipher);
            string json = Encoding.UTF8.GetString(plain, MusicPrefixLength, plain.Length - MusicPrefixLength);
            return NcmMetadataParser.Parse(json);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or System.Text.Json.JsonException)
        {
            throw new NcmDecodeException("corrupt NCM metadata", ex);
        }
        catch (CryptographicException ex)
        {
            throw new NcmDecodeException("corrupt NCM metadata", ex);
        }
    }

    private static byte[]? ReadCover(Stream source)
    {
        int coverFrameLength = ReadInt32(source);
        int imageLength = ReadInt32(source);

        byte[]? cover = null;
        if (imageLength > 0)
        {
            cover = ReadExact(source, imageLength);
        }

        // Skip any trailing gap between the declared frame and the image bytes.
        int gap = coverFrameLength - imageLength;
        if (gap > 0)
        {
            Skip(source, gap);
        }

        return cover;
    }

    private static async Task<AudioFormat> DecryptBodyAsync(
        Stream source, Stream output, byte[] keyBox, CancellationToken ct)
    {
        var buffer = new byte[BodyChunkSize];
        long position = 0;
        AudioFormat format = AudioFormat.Flac; // reference default
        bool firstChunk = true;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int read = await source.ReadAsync(buffer.AsMemory(0, BodyChunkSize), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                int j = (int)((position + i + 1) & 0xff);
                buffer[i] ^= keyBox[(keyBox[j] + keyBox[(keyBox[j] + j) & 0xff]) & 0xff];
            }
            position += read;

            if (firstChunk)
            {
                format = SniffFormat(buffer.AsSpan(0, read));
                firstChunk = false;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return format;
    }

    // ponytail: provisional inline magic-byte sniff. Task 7 routes format detection through the
    // authoritative IFormatSniffer; until then this mirrors the reference decoder (defaults to FLAC).
    private static AudioFormat SniffFormat(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 4 && head[0] == 0x66 && head[1] == 0x4C && head[2] == 0x61 && head[3] == 0x43)
        {
            return AudioFormat.Flac;
        }
        if (head.Length >= 3 && head[0] == 0x49 && head[1] == 0x44 && head[2] == 0x33)
        {
            return AudioFormat.Mp3;
        }
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] == 0xFB || head[1] == 0xF3 || head[1] == 0xF2))
        {
            return AudioFormat.Mp3;
        }
        return AudioFormat.Flac;
    }

    /// <summary>
    /// Builds the 256-byte RC4 keybox from the decrypted RC4 key, using NetEase's modified
    /// key-scheduling algorithm. Internal so tests can verify the schedule directly.
    /// </summary>
    internal static byte[] BuildKeyBox(byte[] rc4Key)
    {
        var box = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            box[i] = (byte)i;
        }

        byte last = 0;
        int offset = 0;
        for (int i = 0; i < 256; i++)
        {
            byte swap = box[i];
            byte c = (byte)((swap + last + rc4Key[offset]) & 0xff);
            offset++;
            if (offset >= rc4Key.Length)
            {
                offset = 0;
            }
            box[i] = box[c];
            box[c] = swap;
            last = c;
        }

        return box;
    }

    private static byte[] AesEcbDecrypt(byte[] key, byte[] cipher)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        return aes.DecryptEcb(cipher, PaddingMode.PKCS7);
    }

    private static int ReadInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExact(stream, buffer);
        return BitConverter.ToInt32(buffer);
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        var buffer = new byte[count];
        ReadExact(stream, buffer);
        return buffer;
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                throw new EndOfStreamException("unexpected end of NCM stream");
            }
            total += read;
        }
    }

    private static void Skip(Stream stream, int count)
    {
        // Read-and-discard rather than Seek: keeps the FileStream's internal read buffer in a
        // consistent state when interleaved with the synchronous header reads above, and lets a
        // truncated trailer surface as an EndOfStreamException (mapped to a truncation error).
        Span<byte> scratch = stackalloc byte[256];
        int remaining = count;
        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, scratch.Length);
            int read = stream.Read(scratch[..chunk]);
            if (read == 0)
            {
                throw new EndOfStreamException("unexpected end of NCM stream while skipping");
            }
            remaining -= read;
        }
    }
}
