using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;
using FreeBird.Core.Processing;
using FreeBird.Core.Sidecar;
using Serilog;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// v3.4 T18 — Regression guard for the v3.2.0 sidecar contract.
///
/// The v3.2.0 schema (T07) introduced two skip-decision fields on top of the v1 5-field
/// sidecar so the watch loop can decide whether a quarantined source has been edited
/// since it failed:
///
///   * <c>source:</c>        — absolute path of the offending .uc / .uc! file
///   * <c>source_size:</c>   — file length in bytes when quarantine occurred
///   * <c>source_mtime:</c>  — last-write time (ISO-8601 round-trip) when quarantine occurred
///
/// v3.4 added per-input-dir watch supervision + the OutputPathMutexPool around the
/// FileProcessor critical section. Neither change touches the sidecar writer in theory,
/// but a hidden regression here would silently break the watch-skip logic (R7 in plan)
/// and re-introduce the infinite-retry bug that v3.2 fixed.
///
/// This test pins the contract two ways:
///   1. Unit-level: invoke <c>FileProcessor.BuildSidecarContentForTesting</c> directly
///      with a real on-disk source and parse the result with the production
///      <see cref="TextSidecarReader"/>. Asserts size + mtime + source path match.
///   2. End-to-end: feed a corrupted .uc through <c>ScanRunner</c> with L1 integrity
///      and parse the resulting <c>.freebird-failed/*.txt</c> with the production
///      reader. Asserts the same three fields plus the v3 <c>version: 3</c> marker.
///
/// If either assertion breaks, v3.2.0 watch-skip is broken and `SourceUnchangedSinceFailure`
/// will not match → infinite quarantine churn returns.
/// </summary>
[Collection("RunnerOverride")]
public class SidecarRegressionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;
    private readonly StemNamerTestOverride _namerOverride = new();

    public SidecarRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-sidecar-regress-{Guid.NewGuid():N}");
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
    public async Task BuildSidecarContent_V32Fields_PresentAndParseable()
    {
        // Lay down a real source file so size + mtime are real, not the
        // vanished-source fallback (size=0, mtime=epoch).
        var sourcePath = Path.Combine(_inputDir, "fixture.uc");
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        await File.WriteAllBytesAsync(sourcePath, payload);

        // Capture mtime of the source AFTER write so the assertion below has the right
        // reference value. The sidecar reads FileInfo.LastWriteTimeUtc internally.
        var expectedSize = payload.Length;
        var expectedMtime = new FileInfo(sourcePath).LastWriteTimeUtc;

        var logger = new LoggerConfiguration().CreateLogger();
        var content = FileProcessor.BuildSidecarContentForTesting(
            sourcePath,
            AudioFormat.Mp3,
            IntegrityLevel.L1,
            reason: "integrity check failed: synthetic regression test",
            logger);

        // Sanity-check the raw text — the literal field names are part of the contract,
        // not just whatever the reader happens to map. Any rename/removal would break
        // downstream tooling that may grep these sidecars by hand.
        content.Should().Contain("source:", "v3.2.0 contract: source field name");
        content.Should().Contain("source_size:", "v3.2.0 contract: source_size field name");
        content.Should().Contain("source_mtime:", "v3.2.0 contract: source_mtime field name");
        content.Should().Contain("version: 3", "v3.x writers stamp version: 3");
        content.Should().Contain(sourcePath, "source path must be the absolute .uc path");

        // Round-trip through the production reader to prove the writer + parser
        // still agree on the field semantics.
        var sidecarPath = Path.Combine(_tempDir, "rt-sidecar.txt");
        await File.WriteAllTextAsync(sidecarPath, content);

        var reader = new TextSidecarReader();
        var record = await reader.TryReadAsync(sidecarPath);

        record.Should().NotBeNull("v3.x sidecar must parse with the production reader");
        record!.Source.Should().Be(sourcePath, "round-trip preserves source path");
        record.SourceSize.Should().Be(expectedSize,
            "v3.2.0 contract: source_size must reflect on-disk size at write time");
        record.SourceMtime.Should().NotBeNull("v3.2.0 contract: source_mtime must be populated");
        record.SourceMtime!.Value.UtcDateTime.Should().BeCloseTo(
            expectedMtime,
            TimeSpan.FromSeconds(1),
            "round-trip preserves mtime to second precision (ISO-8601 round-trip)");
        record.Version.Should().Be(3, "v3.x writer must emit version: 3");
        record.Format.Should().Be(AudioFormat.Mp3, "format round-trips through enum");
        record.IntegrityLevel.Should().Be(IntegrityLevel.L1, "integrity level round-trips");
    }

    [Fact]
    public async Task ScanQuarantine_EmitsV32Sidecar_WithRealSourceFields()
    {
        // Build a corrupted .uc that XOR-decodes to garbage-but-format-sniffable bytes,
        // forcing the L1 integrity check to quarantine + write a sidecar.
        var ucName = "regression-corrupt.uc";
        var ucPath = Path.Combine(_inputDir, ucName);

        // 256 bytes of 0xAA decrypt to known non-magic — sniffer returns Unknown OR
        // a format that fails L1; either way the quarantine + sidecar path runs.
        // Mirror the corruption pattern used in ScanRunnerEndToEndTests.
        var encrypted = new byte[256];
        for (int i = 0; i < encrypted.Length; i++) { encrypted[i] = (byte)(0xAA ^ 0xA3); }
        await File.WriteAllBytesAsync(ucPath, encrypted);

        var sourceSizeAtWrite = new FileInfo(ucPath).Length;
        var sourceMtimeAtWrite = new FileInfo(ucPath).LastWriteTimeUtc;

        var exit = await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.L1, concurrency: 1,
            collision: CollisionPolicy.Skip, verbose: false);

        // We expect a non-success exit when at least one file is quarantined.
        exit.Should().Be(ScanRunner.ExitFailures,
            "corrupted-source quarantine returns ExitFailures");

        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        Directory.Exists(failedDir).Should().BeTrue();

        var sidecars = Directory.GetFiles(failedDir, "*.txt");
        sidecars.Should().HaveCount(1, "exactly one sidecar per quarantined file");

        var reader = new TextSidecarReader();
        var record = await reader.TryReadAsync(sidecars[0]);

        record.Should().NotBeNull(
            "the production reader must parse what the production writer emitted — " +
            "if this breaks, the watch-skip decider will start re-quarantining every poll");

        // R7 / v3.2.0 contract: the watch-skip path keys off these exact fields.
        record!.Source.Should().Be(ucPath,
            "source: must be the absolute input .uc path, not the staging or quarantined path");
        record.SourceSize.Should().Be(sourceSizeAtWrite,
            "source_size: must match the input file's on-disk size when the sidecar was written");
        record.SourceMtime.Should().NotBeNull("source_mtime: must be populated (not v1 fallback)");
        record.SourceMtime!.Value.UtcDateTime.Should().BeCloseTo(
            sourceMtimeAtWrite,
            TimeSpan.FromSeconds(2),
            "source_mtime: must match the input file's mtime at write time");
        record.Version.Should().Be(3, "v3.x writer must emit version: 3");
    }
}
