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
public class ScanRunnerEndToEndTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

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
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Mp3_DotUcSuffix_DecodesAndRoundTripsBitForBit()
    {
        var ucPath = await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, _inputDir, "42-song.uc");

        var exitCode = await ScanRunner.RunAsync(
            _inputDir, _outputDir,
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
            _inputDir, _outputDir,
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
            _inputDir, _outputDir,
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
            _inputDir, _outputDir,
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
            _inputDir, _outputDir,
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
            _inputDir, _outputDir,
            IntegrityLevel.L1, 1, CollisionPolicy.Overwrite, false);

        exitCode.Should().Be(ScanRunner.ExitOk);
        var bytes = await File.ReadAllBytesAsync(existing);
        var fixtureBytes = await File.ReadAllBytesAsync(TestFixtures.SampleMp3Path);
        bytes.Should().Equal(fixtureBytes);
    }
}
