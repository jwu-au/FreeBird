using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Fixtures;

public class FixturesTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-fixtures-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SampleMp3Path_FileExists()
    {
        File.Exists(Fixtures.SampleMp3Path).Should().BeTrue($"fixture missing at {Fixtures.SampleMp3Path}");
    }

    [Fact]
    public void SampleFlacPath_FileExists()
    {
        File.Exists(Fixtures.SampleFlacPath).Should().BeTrue($"fixture missing at {Fixtures.SampleFlacPath}");
    }

    [Fact]
    public async Task ReadFixtureAsEncryptedAsync_Mp3_IsSizeMatchedAndNotPlaintext()
    {
        var encrypted = await Fixtures.ReadFixtureAsEncryptedAsync(Fixtures.SampleMp3Path);
        var original = await File.ReadAllBytesAsync(Fixtures.SampleMp3Path);

        encrypted.Length.Should().Be(original.Length);
        // First 3 bytes of MP3 fixture are 'I' 'D' '3' (0x49 0x44 0x33).
        // After XOR 0xA3: 0xEA 0xE7 0x90 — must not be the same as ID3 magic.
        encrypted[0].Should().Be((byte)(original[0] ^ 0xA3));
        encrypted[1].Should().Be((byte)(original[1] ^ 0xA3));
        encrypted[2].Should().Be((byte)(original[2] ^ 0xA3));
    }

    [Fact]
    public async Task ReadFixtureAsEncryptedAsync_Flac_FirstBytesAreXorredFlacMagic()
    {
        var encrypted = await Fixtures.ReadFixtureAsEncryptedAsync(Fixtures.SampleFlacPath);
        // FLAC magic: f L a C = 0x66 0x4C 0x61 0x43; XOR 0xA3 = 0xC5 0xEF 0xC2 0xE0
        encrypted[0].Should().Be(0xC5);
        encrypted[1].Should().Be(0xEF);
        encrypted[2].Should().Be(0xC2);
        encrypted[3].Should().Be(0xE0);
    }

    [Fact]
    public async Task WriteFixtureAsUcAsync_CreatesFileWithCorrectName()
    {
        var ucPath = await Fixtures.WriteFixtureAsUcAsync(
            Fixtures.SampleMp3Path, _tempDir, "12345-test.uc");
        File.Exists(ucPath).Should().BeTrue();
        ucPath.Should().EndWith("12345-test.uc");
        new FileInfo(ucPath).Length.Should().Be(new FileInfo(Fixtures.SampleMp3Path).Length);
    }

    [Fact]
    public async Task WriteFixtureAsUcAsync_PlusBangSuffix_AlsoWorks()
    {
        var ucPath = await Fixtures.WriteFixtureAsUcAsync(
            Fixtures.SampleFlacPath, _tempDir, "99999-mac.uc!");
        File.Exists(ucPath).Should().BeTrue();
        ucPath.Should().EndWith("99999-mac.uc!");
    }

    [Fact]
    public async Task ReadFixtureAsEncryptedAsync_NonexistentPath_Throws()
    {
        Func<Task> act = () => Fixtures.ReadFixtureAsEncryptedAsync("/nonexistent/file.mp3");
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    /// <summary>
    /// End-to-end smoke: encrypt a fixture, decrypt with XorDecoder, and verify byte-for-byte round-trip.
    /// Proves the test infrastructure is sound for downstream T12/T13 integration tests.
    /// </summary>
    [Fact]
    public async Task RoundTrip_EncryptedFixture_DecryptsToOriginal()
    {
        var ucPath = await Fixtures.WriteFixtureAsUcAsync(
            Fixtures.SampleMp3Path, _tempDir, "roundtrip.uc");

        var decoder = new XorDecoder();
        var outputPath = Path.Combine(_tempDir, "roundtrip.mp3");
        await using (var input = File.OpenRead(ucPath))
        await using (var output = File.Create(outputPath))
        {
            await decoder.DecodeAsync(input, output);
        }

        var decrypted = await File.ReadAllBytesAsync(outputPath);
        var original = await File.ReadAllBytesAsync(Fixtures.SampleMp3Path);
        decrypted.Should().Equal(original);
    }
}
