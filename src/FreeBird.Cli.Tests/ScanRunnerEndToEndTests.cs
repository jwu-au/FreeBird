using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T20: full end-to-end tests using real audio fixtures through the entire CLI pipeline.
/// Verifies XOR roundtrip integrity and output file structure for MP3 and FLAC.
/// </summary>
[Collection("GlobalStaticState")]
public class ScanRunnerEndToEndTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;
    // T13: v1/v2 stem-based outputs (e.g. "42-song.mp3") — pin StemBasedFileNamer
    // as the IFileNamer instead of the v3 default MetadataAwareFileNamer.
    private readonly StemNamerTestOverride _namerOverride = new();

    public ScanRunnerEndToEndTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-e2e-{Guid.NewGuid():N}");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        _namerOverride.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Mp3_DotUcSuffix_DecodesAndRoundTripsBitForBit()
    {
        var ucPath = await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, _inputDir, "42-song.uc");

        var exitCode = await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.L1,
            concurrency: 1,
            collision: CollisionPolicy.Skip,
            verbose: false);

        exitCode.Should().Be(ScanRunner.ExitOk);

        var expectedOut = Path.Combine(_outputDir, "42-song.mp3");
        File.Exists(expectedOut).Should().BeTrue();

        var produced = await File.ReadAllBytesAsync(expectedOut);
        var original = await File.ReadAllBytesAsync(TestFixtures.SampleMp3Path);
        produced.Should().Equal(original, "the XOR pipeline must roundtrip the fixture exactly");

        // Staging cleaned up
        var stagingDir = Path.Combine(_outputDir, ".freebird-staging");
        if (Directory.Exists(stagingDir))
        {
            Directory.GetFiles(stagingDir).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Mp3_DotUcBangSuffix_DecodesCorrectly()
    {
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, _inputDir, "99-mac.uc!");

        var exitCode = await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.L1, 1, CollisionPolicy.Skip, false);

        exitCode.Should().Be(ScanRunner.ExitOk);
        File.Exists(Path.Combine(_outputDir, "99-mac.mp3")).Should().BeTrue();
    }

    [Fact]
    public async Task Flac_DecodesAndRoundTripsBitForBit()
    {
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleFlacPath, _inputDir, "flac-test.uc");

        var exitCode = await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.L1,
            1, CollisionPolicy.Skip, false);

        exitCode.Should().Be(ScanRunner.ExitOk);

        var expectedOut = Path.Combine(_outputDir, "flac-test.flac");
        File.Exists(expectedOut).Should().BeTrue();

        var produced = await File.ReadAllBytesAsync(expectedOut);
        var original = await File.ReadAllBytesAsync(TestFixtures.SampleFlacPath);
        produced.Should().Equal(original);
    }

    [Fact]
    public async Task MultipleFiles_AllSucceed_OutputDirHasAll()
    {
        for (int i = 0; i < 4; i++)
        {
            await TestFixtures.WriteFixtureAsUcAsync(
                TestFixtures.SampleMp3Path, _inputDir, $"file{i}.uc");
        }

        var exitCode = await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.L1, 2, CollisionPolicy.Skip, false);

        exitCode.Should().Be(ScanRunner.ExitOk);
        for (int i = 0; i < 4; i++)
        {
            File.Exists(Path.Combine(_outputDir, $"file{i}.mp3")).Should().BeTrue();
        }
    }

    [Fact]
    public async Task OutputExists_SkipPolicy_DoesNotOverwrite_StillExitOk()
    {
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, _inputDir, "42-song.uc");

        // Pre-create the output file with sentinel content
        var existing = Path.Combine(_outputDir, "42-song.mp3");
        await File.WriteAllTextAsync(existing, "SENTINEL");

        var exitCode = await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.L1, 1, CollisionPolicy.Skip, false);

        exitCode.Should().Be(ScanRunner.ExitOk);
        (await File.ReadAllTextAsync(existing)).Should().Be("SENTINEL", "skip policy must preserve existing file");
    }

    [Fact]
    public async Task OutputExists_OverwritePolicy_ReplacesContent()
    {
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, _inputDir, "42-song.uc");

        var existing = Path.Combine(_outputDir, "42-song.mp3");
        await File.WriteAllTextAsync(existing, "OLD");

        var exitCode = await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.L1, 1, CollisionPolicy.Overwrite, false);

        exitCode.Should().Be(ScanRunner.ExitOk);
        var bytes = await File.ReadAllBytesAsync(existing);
        var fixtureBytes = await File.ReadAllBytesAsync(TestFixtures.SampleMp3Path);
        bytes.Should().Equal(fixtureBytes);
    }

    [Fact]
    public async Task Mp3_L1_Integrity_DetectsCorruption_QuarantinesAndExitsWithFailures()
    {
        // Read valid MP3 fixture, smash the first 256 bytes (destroys ID3 header AND any usable
        // MP3 frame sync) but leave the rest intact. The format sniffer would then return Unknown
        // (no recognizable magic) OR Mp3 if a sync byte happens to appear later — either way the
        // integrity / sniff layer correctly rejects it and the file ends up in .freebird-failed.
        var fixtureBytes = await File.ReadAllBytesAsync(TestFixtures.SampleMp3Path);
        var rng = new Random(42); // deterministic
        for (int i = 0; i < 256 && i < fixtureBytes.Length; i++)
        {
            // Force first 256 bytes to a known non-magic pattern (avoid accidentally hitting
            // ID3/sync/FLAC/M4A magic). 0xAA is not in any of our magic byte sequences.
            fixtureBytes[i] = 0xAA;
        }

        // XOR-encrypt the corrupted blob to make a .uc
        var encrypted = new byte[fixtureBytes.Length];
        for (int i = 0; i < fixtureBytes.Length; i++) { encrypted[i] = (byte)(fixtureBytes[i] ^ 0xA3); }
        var ucPath = Path.Combine(_inputDir, "bad-mp3.uc");
        await File.WriteAllBytesAsync(ucPath, encrypted);

        var exitCode = await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.L1,
            1, CollisionPolicy.Skip, false, false);

        exitCode.Should().Be(ScanRunner.ExitFailures);

        // No valid MP3 output should be produced
        File.Exists(Path.Combine(_outputDir, "bad-mp3.mp3")).Should().BeFalse();

        // Quarantine: there should be both a binary and a sidecar .txt
        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        Directory.Exists(failedDir).Should().BeTrue("failed dir should be created for corrupted input");
        var failedFiles = Directory.GetFiles(failedDir);
        failedFiles.Should().NotBeEmpty();
        failedFiles.Should().Contain(f => f.EndsWith(".txt"), "sidecar must exist");

        // Sidecar must contain the 5 spec fields
        var sidecar = failedFiles.First(f => f.EndsWith(".txt"));
        var content = await File.ReadAllTextAsync(sidecar);
        content.Should().Contain("timestamp:");
        content.Should().Contain("source:");
        content.Should().Contain("format:");
        content.Should().Contain("integrity:");
        content.Should().Contain("reason:");
        content.Should().Contain("bad-mp3.uc");
    }
}
