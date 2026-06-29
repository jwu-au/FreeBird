using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FreeBird.Core.Tests.Ncm;

/// <summary>
/// Metadata for a generated <c>.ncm</c> test fixture. Mirrors the fields the real
/// NetEase NCM metadata JSON carries that the decoder reads.
/// </summary>
public sealed record NcmFixtureMeta(
    long MusicId,
    string MusicName,
    string Album,
    (string Name, long Id)[] Artists,
    string Format,
    long AlbumId = 0,
    int Bitrate = 320000,
    int Duration = 0);

/// <summary>
/// TEST-ONLY encoder that produces a valid <c>.ncm</c> byte stream. It is the EXACT
/// inverse of the NCM decode algorithm in NcmdumpCSharp's <c>NeteaseCrypt</c>, so the
/// real decoder (built in later tasks) round-trips its output byte-for-byte.
///
/// This deliberately lives in the test project — it is never production code.
/// </summary>
public static class NcmTestEncoder
{
    private static readonly byte[] CoreKey = "hzHRAmso5kInbaxW"u8.ToArray();
    private static readonly byte[] ModifyKey = "#14ljk_!\\]&0U<'("u8.ToArray();

    // The decoder drops the first 17 bytes of the AES-decrypted key block. Real files
    // use "neteasecloudmusic" (exactly 17 ASCII chars) so we match them.
    private static readonly byte[] KeyPrefix = "neteasecloudmusic"u8.ToArray();

    // The decoder strips this 22-byte ASCII prefix before base64-decoding the metadata.
    private static readonly byte[] MetadataPrefix = "163 key(Don't modify):"u8.ToArray();

    private static readonly byte[] MusicPrefix = "music:"u8.ToArray();

    // An arbitrary RC4 key the keybox is built from. Length need not be 256; the
    // keybox schedule wraps the offset at the key length.
    private static readonly byte[] Rc4Key =
        "fixtureRc4Key-abcdefghijklmnopqrstuvwxyz0123456789"u8.ToArray();

    /// <summary>
    /// Encodes <paramref name="body"/> (raw audio bytes, e.g. sample.flac contents) into a
    /// <c>.ncm</c> byte stream.
    /// </summary>
    /// <param name="body">Plaintext audio bytes to embed and RC4-encrypt.</param>
    /// <param name="meta">Metadata to embed, or <c>null</c> to write a zero-length metadata block.</param>
    /// <param name="cover">Cover image bytes, or <c>null</c> to write a zero-length cover block.</param>
    public static byte[] Encode(byte[] body, NcmFixtureMeta? meta, byte[]? cover)
    {
        ArgumentNullException.ThrowIfNull(body);

        using var output = new MemoryStream();

        // 1. Magic: two Int32-LE values + 2 filler bytes (decoder skips them).
        WriteInt32(output, 0x4e455443);
        WriteInt32(output, 0x4d414446);
        output.WriteByte(0x00);
        output.WriteByte(0x00);

        // 2. RC4 key block: AES-ECB(coreKey, prefix + rc4Key), then XOR 0x64.
        byte[] keyPlain = Concat(KeyPrefix, Rc4Key);
        byte[] keyCipher = AesEcbEncrypt(CoreKey, keyPlain);
        for (int i = 0; i < keyCipher.Length; i++)
        {
            keyCipher[i] ^= 0x64;
        }
        WriteInt32(output, keyCipher.Length);
        output.Write(keyCipher);

        // 3. Metadata block (or zero-length).
        if (meta is null)
        {
            WriteInt32(output, 0);
        }
        else
        {
            byte[] metaBlock = BuildMetadataBlock(meta);
            WriteInt32(output, metaBlock.Length);
            output.Write(metaBlock);
        }

        // 4. CRC32 placeholder (4 bytes) + image version (1 byte) = 5 bytes the decoder skips.
        output.Write(new byte[5]);

        // 5. Cover: coverFrameLength (Int32-LE) then imageLength (Int32-LE), then image bytes.
        // Setting both equal means no trailing gap to seek over.
        if (cover is { Length: > 0 })
        {
            WriteInt32(output, cover.Length);
            WriteInt32(output, cover.Length);
            output.Write(cover);
        }
        else
        {
            WriteInt32(output, 0);
            WriteInt32(output, 0);
        }

        // 6. Body: RC4-XOR with the keybox using the ABSOLUTE body byte index. The reference
        // decoder accumulates `position` across 0x8000 chunks and uses (position+i+1)&0xff,
        // so the counter is effectively absolute. RC4-XOR is symmetric.
        byte[] keyBox = BuildKeyBox(Rc4Key);
        byte[] encBody = new byte[body.Length];
        for (int i = 0; i < body.Length; i++)
        {
            int j = (i + 1) & 0xff;
            encBody[i] = (byte)(body[i] ^ keyBox[(keyBox[j] + keyBox[(keyBox[j] + j) & 0xff]) & 0xff]);
        }
        output.Write(encBody);

        return output.ToArray();
    }

    private static byte[] BuildMetadataBlock(NcmFixtureMeta meta)
    {
        string json = BuildJson(meta);
        byte[] jsonWithPrefix = Concat(MusicPrefix, Encoding.UTF8.GetBytes(json));
        byte[] cipher = AesEcbEncrypt(ModifyKey, jsonWithPrefix);
        byte[] base64 = Encoding.ASCII.GetBytes(Convert.ToBase64String(cipher));
        byte[] blob = Concat(MetadataPrefix, base64);
        for (int i = 0; i < blob.Length; i++)
        {
            blob[i] ^= 0x63;
        }
        return blob;
    }

    private static string BuildJson(NcmFixtureMeta meta)
    {
        var artist = new StringBuilder("[");
        for (int i = 0; i < meta.Artists.Length; i++)
        {
            if (i > 0)
            {
                artist.Append(',');
            }
            artist.Append('[').Append(JsonString(meta.Artists[i].Name)).Append(',')
                  .Append(meta.Artists[i].Id).Append(']');
        }
        artist.Append(']');

        return "{"
            + "\"musicId\":" + meta.MusicId + ","
            + "\"musicName\":" + JsonString(meta.MusicName) + ","
            + "\"artist\":" + artist + ","
            + "\"album\":" + JsonString(meta.Album) + ","
            + "\"albumId\":" + meta.AlbumId + ","
            + "\"bitrate\":" + meta.Bitrate + ","
            + "\"duration\":" + meta.Duration + ","
            + "\"format\":" + JsonString(meta.Format)
            + "}";
    }

    // Minimal JSON string escaper: only the characters that can break the structure.
    private static string JsonString(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (char c in value)
        {
            if (c == '"' || c == '\\')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        builder.Append('"');
        return builder.ToString();
    }

    private static byte[] BuildKeyBox(byte[] rc4Key)
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

    private static byte[] AesEcbEncrypt(byte[] key, byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        return aes.EncryptEcb(plain, PaddingMode.PKCS7);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }
}
