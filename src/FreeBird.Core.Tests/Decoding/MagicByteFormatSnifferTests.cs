using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Decoding;

public class MagicByteFormatSnifferTests
{
    private readonly MagicByteFormatSniffer _sut = new();

    // --- Pure SniffBytes tests ---

    [Fact]
    public void SniffBytes_FlacMagic_ReturnsFlac()
    {
        var bytes = new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22 };
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.Flac);
    }

    [Fact]
    public void SniffBytes_Id3v2Magic_ReturnsMp3()
    {
        var bytes = new byte[] { 0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.Mp3);
    }

    [Theory]
    [InlineData((byte)0xFF, (byte)0xFB)] // MPEG 1 Layer 3
    [InlineData((byte)0xFF, (byte)0xF3)] // MPEG 2 Layer 3
    [InlineData((byte)0xFF, (byte)0xF2)] // MPEG 2.5 Layer 3
    [InlineData((byte)0xFF, (byte)0xE0)] // edge: just the 3 high bits
    public void SniffBytes_RawMpegFrameSync_ReturnsMp3(byte b0, byte b1)
    {
        var bytes = new byte[] { b0, b1, 0x00, 0x00 };
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.Mp3);
    }

    [Fact]
    public void SniffBytes_M4aFtypAtOffset4_ReturnsM4a()
    {
        var bytes = new byte[] { 0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20 };
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.M4a);
    }

    [Fact]
    public void SniffBytes_RandomGarbage_ReturnsUnknown()
    {
        var bytes = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x11, 0x22, 0x33, 0x44 };
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.Unknown);
    }

    [Fact]
    public void SniffBytes_EmptySpan_ReturnsUnknown()
    {
        MagicByteFormatSniffer.SniffBytes(ReadOnlySpan<byte>.Empty).Should().Be(AudioFormat.Unknown);
    }

    [Fact]
    public void SniffBytes_OneByte_ReturnsUnknown()
    {
        var bytes = new byte[] { 0xFF };
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.Unknown);
    }

    [Fact]
    public void SniffBytes_TwoBytesMpegSync_StillReturnsMp3()
    {
        // 2 bytes is the minimum for raw-MPEG detection
        var bytes = new byte[] { 0xFF, 0xFB };
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.Mp3);
    }

    [Fact]
    public void SniffBytes_ThreeBytesId3_ReturnsMp3()
    {
        var bytes = new byte[] { 0x49, 0x44, 0x33 };
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.Mp3);
    }

    [Fact]
    public void SniffBytes_FlacWithExtraBytes_StillFlac()
    {
        var bytes = new byte[100];
        bytes[0] = 0x66; bytes[1] = 0x4C; bytes[2] = 0x61; bytes[3] = 0x43;
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.Flac);
    }

    [Fact]
    public void SniffBytes_AlmostFlacButTypo_ReturnsUnknown()
    {
        // first char wrong
        var bytes = new byte[] { 0x67, 0x4C, 0x61, 0x43 };
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.Unknown);
    }

    [Fact]
    public void SniffBytes_FtypAtOffset0_DoesNotMatchM4a()
    {
        // m4a detection requires ftyp at offset 4, not 0
        var bytes = new byte[] { 0x66, 0x74, 0x79, 0x70, 0x00, 0x00, 0x00, 0x00 };
        MagicByteFormatSniffer.SniffBytes(bytes).Should().Be(AudioFormat.Unknown);
    }

    // --- IO-based SniffAsync tests using temp files ---

    [Fact]
    public async Task SniffAsync_FlacFile_ReturnsFlac()
    {
        var path = Path.Combine(Path.GetTempPath(), $"freebird-test-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x00 });
            (await _sut.SniffAsync(path)).Should().Be(AudioFormat.Flac);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SniffAsync_EmptyFile_ReturnsUnknown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"freebird-test-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(path, Array.Empty<byte>());
            (await _sut.SniffAsync(path)).Should().Be(AudioFormat.Unknown);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SniffAsync_ShortFile_HandlesGracefully()
    {
        // File shorter than 12 bytes but enough for ID3
        var path = Path.Combine(Path.GetTempPath(), $"freebird-test-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[] { 0x49, 0x44, 0x33 });
            (await _sut.SniffAsync(path)).Should().Be(AudioFormat.Mp3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SniffAsync_FileNotFound_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"freebird-nonexistent-{Guid.NewGuid():N}.tmp");
        Func<Task> act = () => _sut.SniffAsync(path);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task SniffAsync_NullPath_Throws()
    {
        Func<Task> act = () => _sut.SniffAsync(null!);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
