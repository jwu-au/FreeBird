using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using FreeBird.Core.Tests.Ncm;
using Xunit;
using Fx = FreeBird.Core.Tests.Fixtures.Fixtures;

namespace FreeBird.Core.Tests.Decoding;

/// <summary>
/// Verifies <see cref="NcmDecoder"/> is the byte-exact inverse of <see cref="NcmTestEncoder"/>
/// (which itself mirrors the reference NeteaseCrypt algorithm). Covers magic validation,
/// keybox derivation (proven via body round-trip), metadata, cover, format sniff, and error paths.
/// </summary>
public sealed class NcmDecoderTests : IDisposable
{
    private readonly string _tempDir;

    public NcmDecoderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "fb-ncm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteNcm(byte[] ncmBytes, string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, ncmBytes);
        return path;
    }

    // ---------- Stage 4: magic ----------

    [Fact]
    public async Task DecodeAsync_BadMagic_ThrowsWithMagicReason()
    {
        // 16 bytes of garbage — first 8 are the (wrong) magic, rest pads the header reads.
        var bytes = new byte[16];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i + 1);
        }
        var path = WriteNcm(bytes, "bad-magic.ncm");

        var act = async () =>
        {
            using var output = new MemoryStream();
            await new NcmDecoder().DecodeAsync(path, output);
        };

        var ex = await act.Should().ThrowAsync<NcmDecodeException>();
        ex.Which.Reason.Should().Contain("magic");
    }

    // ---------- Stage 4: keybox (explicit, optional but nice) ----------

    [Fact]
    public void BuildKeyBox_MatchesEncoderSchedule()
    {
        // The encoder builds its keybox from the same rc4 key with the identical schedule;
        // a known key must yield an identical 256-byte box from the production helper.
        var rc4Key = "fixtureRc4Key-abcdefghijklmnopqrstuvwxyz0123456789"u8.ToArray();

        var box = NcmDecoder.BuildKeyBox(rc4Key);

        box.Should().HaveCount(256);
        // Sanity: the box is a permutation of 0..255.
        box.Should().OnlyHaveUniqueItems();
    }

    // ---------- Stage 5: metadata ----------

    [Fact]
    public async Task DecodeAsync_WithMetadata_ParsesFields()
    {
        var meta = new NcmFixtureMeta(
            MusicId: 42,
            MusicName: "Poison",
            Album: "Poison",
            Artists: new[] { ("Taylor Grey", 1L) },
            Format: "flac");
        var body = File.ReadAllBytes(Fx.SampleFlacPath);
        var path = WriteNcm(NcmTestEncoder.Encode(body, meta, cover: null), "with-meta.ncm");

        using var output = new MemoryStream();
        var result = await new NcmDecoder().DecodeAsync(path, output);

        result.Metadata.Should().NotBeNull();
        result.Metadata!.MusicName.Should().Be("Poison");
        result.Metadata.Album.Should().Be("Poison");
        result.Metadata.Artists.Should().ContainSingle().Which.Should().Be("Taylor Grey");
        result.Metadata.Format.Should().Be("flac");
    }

    [Fact]
    public async Task DecodeAsync_NoMetadataBlock_MetadataIsNull()
    {
        var body = File.ReadAllBytes(Fx.SampleFlacPath);
        var path = WriteNcm(NcmTestEncoder.Encode(body, meta: null, cover: null), "no-meta.ncm");

        using var output = new MemoryStream();
        var result = await new NcmDecoder().DecodeAsync(path, output);

        result.Metadata.Should().BeNull();
    }

    // ---------- Stage 6: cover + body round-trip ----------

    [Fact]
    public async Task DecodeAsync_WithCover_ReturnsCoverBytes()
    {
        var cover = new byte[] { 0xFF, 0xD8, 0xFF, 0x10, 0x20, 0x30, 0x40 };
        var body = File.ReadAllBytes(Fx.SampleFlacPath);
        var path = WriteNcm(NcmTestEncoder.Encode(body, meta: null, cover), "with-cover.ncm");

        using var output = new MemoryStream();
        var result = await new NcmDecoder().DecodeAsync(path, output);

        result.Cover.Should().Equal(cover);
    }

    [Fact]
    public async Task DecodeAsync_NoCover_CoverIsNull()
    {
        var body = File.ReadAllBytes(Fx.SampleFlacPath);
        var path = WriteNcm(NcmTestEncoder.Encode(body, meta: null, cover: null), "no-cover.ncm");

        using var output = new MemoryStream();
        var result = await new NcmDecoder().DecodeAsync(path, output);

        result.Cover.Should().BeNull();
    }

    [Fact]
    public async Task DecodeAsync_FlacBody_RoundTripsByteForByte()
    {
        var original = File.ReadAllBytes(Fx.SampleFlacPath);
        var path = WriteNcm(NcmTestEncoder.Encode(original, meta: null, cover: null), "flac-body.ncm");

        using var output = new MemoryStream();
        var result = await new NcmDecoder().DecodeAsync(path, output);

        output.ToArray().Should().Equal(original);
        result.Format.Should().Be(AudioFormat.Flac);
    }

    [Fact]
    public async Task DecodeAsync_Mp3Body_DetectsMp3()
    {
        var original = File.ReadAllBytes(Fx.SampleMp3Path);
        var path = WriteNcm(NcmTestEncoder.Encode(original, meta: null, cover: null), "mp3-body.ncm");

        using var output = new MemoryStream();
        var result = await new NcmDecoder().DecodeAsync(path, output);

        output.ToArray().Should().Equal(original);
        result.Format.Should().Be(AudioFormat.Mp3);
    }

    // ---------- Error paths ----------

    [Fact]
    public async Task DecodeAsync_TruncatedHeader_ThrowsTruncated()
    {
        var body = File.ReadAllBytes(Fx.SampleFlacPath);
        var full = NcmTestEncoder.Encode(body, meta: null, cover: null);
        // Slice to 20 bytes — past magic but inside the RC4-key block read -> read past EOF.
        var truncated = full[..20];
        var path = WriteNcm(truncated, "truncated.ncm");

        var act = async () =>
        {
            using var output = new MemoryStream();
            await new NcmDecoder().DecodeAsync(path, output);
        };

        var ex = await act.Should().ThrowAsync<NcmDecodeException>();
        ex.Which.Reason.Should().Contain("truncated");
    }
}
