using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// v3.4 T17 — Multi-input scan end-to-end.
///
/// <c>fb scan dir1 dir2 --output X</c> must (1) iterate every input directory in
/// sequence, (2) collapse all outputs into the single shared output dir, and
/// (3) serialise same-output-path writes through the <c>OutputPathMutexPool</c>
/// (no race, no corruption, no partial writes).
///
/// These tests drive <see cref="ScanRunner.RunAsync"/> directly with the real
/// container so the production wiring (Autofac → OutputPathMutexPool →
/// FileProcessor → AtomicFileWriter) is exercised end-to-end against real XOR
/// fixtures on the real filesystem.
///
/// Uses <see cref="StemNamerTestOverride"/> so output filenames are deterministic
/// (derived from the source stem, e.g. <c>42-song.mp3</c>) rather than going through
/// the metadata-aware namer which would need a network round-trip.
/// </summary>
[Collection("ConsoleRedirect")]
public class ScanMultiInputE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _outputDir;
    private readonly StemNamerTestOverride _namerOverride = new();

    public ScanMultiInputE2ETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-scan-multi-{Guid.NewGuid():N}");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        _namerOverride.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string MakeInputDir(string name)
    {
        var p = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public async Task Scan_TwoInputDirsBothHaveFiles_ProcessesAll_FlatOutputDir()
    {
        // Two distinct stems → two distinct output names → no collision possible.
        var in1 = MakeInputDir("in1");
        var in2 = MakeInputDir("in2");

        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, in1, "100-from-dir1.uc");
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, in2, "200-from-dir2.uc");

        var exit = await ScanRunner.RunAsync(
            new[] { in1, in2 }, _outputDir,
            IntegrityLevel.L1, concurrency: 2,
            collision: CollisionPolicy.Skip, verbose: false);

        exit.Should().Be(ScanRunner.ExitOk,
            "both inputs are valid + both fixtures decode successfully");

        // The single output dir should contain both decoded files, side-by-side,
        // regardless of which input dir they came from.
        File.Exists(Path.Combine(_outputDir, "100-from-dir1.mp3")).Should().BeTrue(
            "file from dir1 must land in the shared output dir");
        File.Exists(Path.Combine(_outputDir, "200-from-dir2.mp3")).Should().BeTrue(
            "file from dir2 must land in the same shared output dir");

        // Sanity-check the staging dir got cleaned up.
        var stagingDir = Path.Combine(_outputDir, ".freebird-staging");
        if (Directory.Exists(stagingDir))
        {
            Directory.GetFiles(stagingDir).Should().BeEmpty(
                "staging must be drained on a clean scan");
        }
    }

    [Fact]
    public async Task Scan_TwoInputDirsSameStem_SerialisedByMutex_FinalOutputIsValid()
    {
        // Both dirs hold the SAME logical file (same stem → same output name).
        // Under v3.4 the OutputPathMutexPool serialises the two writers against
        // the shared output path. With CollisionPolicy.Overwrite the second writer
        // overwrites the first, but it MUST be a clean overwrite — not a half-flushed
        // mix of bytes from both decode runs.
        var in1 = MakeInputDir("in1");
        var in2 = MakeInputDir("in2");

        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, in1, "777-shared.uc");
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, in2, "777-shared.uc");

        var exit = await ScanRunner.RunAsync(
            new[] { in1, in2 }, _outputDir,
            IntegrityLevel.L1, concurrency: 4,
            collision: CollisionPolicy.Overwrite, verbose: false);

        exit.Should().Be(ScanRunner.ExitOk,
            "both decode runs must succeed even when racing on the same output path");

        // Exactly one output file — both writers targeted the same path.
        var outputs = Directory.GetFiles(_outputDir, "*.mp3");
        outputs.Should().HaveCount(1,
            "same stem in both inputs → same output filename → exactly one file on disk");

        // The output must be a clean, complete copy — bit-for-bit equal to the source
        // fixture. If the mutex were missing, two concurrent atomic moves could leave
        // a torn intermediate state OR one write could clobber the other mid-flush.
        var producedBytes = await File.ReadAllBytesAsync(outputs[0]);
        var fixtureBytes = await File.ReadAllBytesAsync(TestFixtures.SampleMp3Path);
        producedBytes.Should().Equal(fixtureBytes,
            "the surviving output must be a bit-for-bit decode — proves no torn write");

        // No leaked staging files.
        var stagingDir = Path.Combine(_outputDir, ".freebird-staging");
        if (Directory.Exists(stagingDir))
        {
            Directory.GetFiles(stagingDir).Should().BeEmpty(
                "no temp file may leak even under contention");
        }
    }

    [Fact]
    public async Task Scan_OneInputMissing_FailsFast_NoFilesProcessed()
    {
        // Per ScanRunner T14/T15 fail-fast: ALL input dirs are validated up-front;
        // a single missing dir aborts the whole run before any work begins.
        var valid = MakeInputDir("valid");
        var missing = Path.Combine(_tempDir, "does-not-exist");

        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, valid, "99-valid-input.uc");

        var exit = await ScanRunner.RunAsync(
            new[] { valid, missing }, _outputDir,
            IntegrityLevel.L1, concurrency: 1,
            collision: CollisionPolicy.Skip, verbose: false);

        exit.Should().Be(ScanRunner.ExitBadArgs,
            "any missing input dir → fail-fast with ExitBadArgs (no partial processing)");

        // The valid dir's file must NOT have been processed — fail-fast means no work.
        File.Exists(Path.Combine(_outputDir, "99-valid-input.mp3")).Should().BeFalse(
            "fail-fast must abort before the valid input is touched");

        var outputFiles = Directory.GetFiles(_outputDir, "*", SearchOption.TopDirectoryOnly);
        outputFiles.Should().BeEmpty(
            "no output files of any kind on a fail-fast bad-args exit");
    }

    [Fact]
    public async Task Scan_ThreeInputDirsAllValid_AllFilesProcessed()
    {
        // Wider variant of the happy path — proves the multi-input loop doesn't
        // silently stop after 2 inputs (off-by-one regression guard).
        var in1 = MakeInputDir("alpha");
        var in2 = MakeInputDir("beta");
        var in3 = MakeInputDir("gamma");

        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, in1, "1-a.uc");
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, in2, "2-b.uc");
        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleMp3Path, in3, "3-c.uc");

        var exit = await ScanRunner.RunAsync(
            new[] { in1, in2, in3 }, _outputDir,
            IntegrityLevel.L1, concurrency: 2,
            collision: CollisionPolicy.Skip, verbose: false);

        exit.Should().Be(ScanRunner.ExitOk);
        File.Exists(Path.Combine(_outputDir, "1-a.mp3")).Should().BeTrue();
        File.Exists(Path.Combine(_outputDir, "2-b.mp3")).Should().BeTrue();
        File.Exists(Path.Combine(_outputDir, "3-c.mp3")).Should().BeTrue();
    }
}
