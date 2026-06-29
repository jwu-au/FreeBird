using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FreeBird.Core.Tests.Fixtures;

namespace FreeBird.Core.Tests.Ncm;

/// <summary>
/// Self-test for <see cref="NcmTestEncoder"/>. Since the real NcmDecoder does not exist
/// yet (built in later tasks), this proves the encoder output is structurally correct by
/// decoding it INLINE with a small local routine that mirrors the reference NCM decoder.
///
/// ponytail: the inline decode below is a temporary structural check for this foundation
/// task. Tasks 4-6 replace it by decoding the same encoder output through the real
/// NcmDecoder (round-tripping the same in-memory fixture). Known ceiling: this duplicates
/// the decode algorithm; upgrade path is to delete it once NcmDecoder exists.
/// </summary>
public class NcmTestEncoderTests
{
    private static readonly byte[] CoreKey = "hzHRAmso5kInbaxW"u8.ToArray();
    private static readonly byte[] ModifyKey = "#14ljk_!\\]&0U<'("u8.ToArray();

    [Fact]
    public void Encode_FlacBody_RoundTripsThroughInlineDecode()
    {
        var body = File.ReadAllBytes(Fixtures.Fixtures.SampleFlacPath);
        var meta = new NcmFixtureMeta(
            MusicId: 12345,
            MusicName: "Test Song",
            Album: "Test Album",
            Artists: new[] { ("Test Artist", 67890L) },
            Format: "flac");
        byte[] cover = { 0xFF, 0xD8, 0xFF };

        byte[] ncm = NcmTestEncoder.Encode(body, meta, cover);

        // --- Inline decode, mirroring NeteaseCrypt.cs ---
        using var stream = new MemoryStream(ncm);

        // 1. Magic: two Int32-LE values, then 2 filler bytes.
        ReadInt32(stream).Should().Be(0x4e455443);
        ReadInt32(stream).Should().Be(0x4d414446);
        stream.Seek(2, SeekOrigin.Current);

        // 2. RC4 key block.
        int keyLen = ReadInt32(stream);
        keyLen.Should().BeGreaterThan(0);
        var keyData = ReadExact(stream, keyLen);
        for (int i = 0; i < keyData.Length; i++)
        {
            keyData[i] ^= 0x64;
        }
        byte[] decryptedKey = AesEcbDecrypt(CoreKey, keyData);
        // First 17 bytes are the dropped prefix; the rest is the rc4 key.
        Encoding.ASCII.GetString(decryptedKey, 0, 17).Should().Be("neteasecloudmusic");
        byte[] rc4Key = decryptedKey[17..];

        // 3. Metadata block.
        int metaLen = ReadInt32(stream);
        metaLen.Should().BeGreaterThan(0);
        var metaData = ReadExact(stream, metaLen);
        for (int i = 0; i < metaData.Length; i++)
        {
            metaData[i] ^= 0x63;
        }
        string base64 = Encoding.UTF8.GetString(metaData, 22, metaData.Length - 22);
        byte[] metaCipher = Convert.FromBase64String(base64);
        byte[] metaPlain = AesEcbDecrypt(ModifyKey, metaCipher);
        string json = Encoding.UTF8.GetString(metaPlain, 6, metaPlain.Length - 6);
        json.Should().Contain("\"musicName\":\"Test Song\"");
        json.Should().Contain("\"album\":\"Test Album\"");
        json.Should().Contain("\"format\":\"flac\"");
        json.Should().Contain("Test Artist");

        // 4. CRC + image version gap.
        stream.Seek(5, SeekOrigin.Current);

        // 5. Cover.
        int coverFrameLength = ReadInt32(stream);
        int imageLength = ReadInt32(stream);
        imageLength.Should().Be(cover.Length);
        var image = ReadExact(stream, imageLength);
        image.Should().Equal(cover);
        stream.Seek(coverFrameLength - imageLength, SeekOrigin.Current);

        // 6. Body: RC4-XOR back to plaintext using the same keybox + absolute counter.
        byte[] encBody = ReadExact(stream, body.Length);
        stream.Position.Should().Be(stream.Length, "the body should consume the rest of the stream");
        byte[] keyBox = BuildKeyBox(rc4Key);
        long position = 0;
        for (int i = 0; i < encBody.Length; i++)
        {
            int j = (int)((position + i + 1) & 0xff);
            encBody[i] ^= keyBox[(keyBox[j] + keyBox[(keyBox[j] + j) & 0xff]) & 0xff];
        }
        encBody.Should().Equal(body, "RC4-XOR is symmetric so re-applying must reproduce the original audio");
    }

    [Fact]
    public void Encode_NoCover_WritesZeroLengthCoverBlock()
    {
        var body = File.ReadAllBytes(Fixtures.Fixtures.SampleFlacPath);
        var meta = new NcmFixtureMeta(1, "X", "Y", new[] { ("Z", 2L) }, "flac");

        byte[] ncm = NcmTestEncoder.Encode(body, meta, cover: null);

        using var stream = new MemoryStream(ncm);
        stream.Seek(8 + 2, SeekOrigin.Begin);
        SkipLengthPrefixed(stream); // key block
        SkipLengthPrefixed(stream); // metadata block
        stream.Seek(5, SeekOrigin.Current);
        ReadInt32(stream).Should().Be(0, "coverFrameLength must be 0 when no cover");
        ReadInt32(stream).Should().Be(0, "imageLength must be 0 when no cover");
    }

    [Fact]
    public void Encode_EmptyMetadata_WritesZeroLengthMetadataBlock()
    {
        var body = File.ReadAllBytes(Fixtures.Fixtures.SampleFlacPath);

        byte[] ncm = NcmTestEncoder.Encode(body, meta: null, cover: null);

        using var stream = new MemoryStream(ncm);
        stream.Seek(8 + 2, SeekOrigin.Begin);
        SkipLengthPrefixed(stream); // key block
        ReadInt32(stream).Should().Be(0, "metadata length must be 0 for the empty-metadata path");
    }

    private static void SkipLengthPrefixed(Stream stream)
    {
        int len = ReadInt32(stream);
        stream.Seek(len, SeekOrigin.Current);
    }

    private static int ReadInt32(Stream stream)
    {
        var buffer = ReadExact(stream, 4);
        return BitConverter.ToInt32(buffer, 0);
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0)
            {
                throw new EndOfStreamException();
            }
            read += n;
        }
        return buffer;
    }

    private static byte[] AesEcbDecrypt(byte[] key, byte[] cipher)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        return aes.DecryptEcb(cipher, PaddingMode.PKCS7);
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
}
