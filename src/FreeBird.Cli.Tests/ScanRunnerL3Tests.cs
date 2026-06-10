using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;
using Xunit;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T21: end-to-end tests covering the L3 (real `flac -t`) integrity path.
/// Auto-skipped when the `flac` binary is not on PATH.
/// </summary>
[Collection("ConsoleRedirect")]
public class ScanRunnerL3Tests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;
    private static readonly bool FlacAvailable = ProbeFlacOnce();
    // T13: stem-based output names ("good.flac", "auto-flac.flac", "corrupt.*") —
    // pin StemBasedFileNamer instead of the v3 default MetadataAwareFileNamer.
    private readonly StemNamerTestOverride _namerOverride = new();

    public ScanRunnerL3Tests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-l3-{Guid.NewGuid():N}");
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

    private static bool ProbeFlacOnce()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "flac",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [SkippableFact]
    public async Task Flac_L3_Integrity_Passes_BitForBitRoundTrip()
    {
        Skip.IfNot(FlacAvailable, "flac binary not on PATH");

        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleFlacPath, _inputDir, "good.uc");

        var exitCode = await ScanRunner.RunAsync(
            _inputDir, _outputDir,
            IntegrityLevel.L3,    // real flac -t path
            concurrency: 1,
            collision: CollisionPolicy.Skip,
            verbose: false,
            quiet: false);

        exitCode.Should().Be(ScanRunner.ExitOk);

        var outPath = Path.Combine(_outputDir, "good.flac");
        File.Exists(outPath).Should().BeTrue("L3 integrity should have passed for valid FLAC");

        var produced = await File.ReadAllBytesAsync(outPath);
        var original = await File.ReadAllBytesAsync(TestFixtures.SampleFlacPath);
        produced.Should().Equal(original);

        // No quarantine subdir should exist (or be empty)
        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        if (Directory.Exists(failedDir))
        {
            Directory.GetFiles(failedDir).Should().BeEmpty();
        }
    }

    [SkippableFact]
    public async Task Flac_Auto_WithFlacAvailable_UsesL3_RoundTripsCorrectly()
    {
        Skip.IfNot(FlacAvailable, "flac binary not on PATH");

        await TestFixtures.WriteFixtureAsUcAsync(
            TestFixtures.SampleFlacPath, _inputDir, "auto-flac.uc");

        var exitCode = await ScanRunner.RunAsync(
            _inputDir, _outputDir,
            IntegrityLevel.Auto,
            1, CollisionPolicy.Skip, false, false);

        exitCode.Should().Be(ScanRunner.ExitOk);
        File.Exists(Path.Combine(_outputDir, "auto-flac.flac")).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Flac_L3_Integrity_DetectsCorruption_QuarantinesAndExitsWithFailures()
    {
        Skip.IfNot(FlacAvailable, "flac binary not on PATH");

        // Build a corrupted FLAC: read original, mutate a byte deep in audio data, then XOR-encrypt to .uc
        var fixtureBytes = await File.ReadAllBytesAsync(TestFixtures.SampleFlacPath);
        // Find a position safely inside audio data (well past STREAMINFO + padding metadata)
        // sample.flac is ~573KB; offset 100_000 is firmly inside audio frames
        var corruptOffset = Math.Min(100_000, fixtureBytes.Length / 2);
        fixtureBytes[corruptOffset] ^= 0xFF;

        // Now XOR-encrypt the entire corrupted blob to make a .uc
        var encrypted = new byte[fixtureBytes.Length];
        for (int i = 0; i < fixtureBytes.Length; i++) { encrypted[i] = (byte)(fixtureBytes[i] ^ 0xA3); }
        var ucPath = Path.Combine(_inputDir, "corrupt.uc");
        await File.WriteAllBytesAsync(ucPath, encrypted);

        var exitCode = await ScanRunner.RunAsync(
            _inputDir, _outputDir,
            IntegrityLevel.L3,
            1, CollisionPolicy.Skip, false, false);

        exitCode.Should().Be(ScanRunner.ExitFailures);

        // The corrupted file should be quarantined, not in output proper
        File.Exists(Path.Combine(_outputDir, "corrupt.flac")).Should().BeFalse();
        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        Directory.Exists(failedDir).Should().BeTrue();
        var quarantined = Directory.GetFiles(failedDir, "corrupt.*");
        quarantined.Should().HaveCount(2, "expected one .flac and one .flac.txt sidecar");
        quarantined.Should().Contain(p => p.EndsWith(".txt"));

        // Sidecar should mention the L3 / flac -t failure
        var sidecar = quarantined.Single(p => p.EndsWith(".txt"));
        var sidecarContent = await File.ReadAllTextAsync(sidecar);
        sidecarContent.Should().Contain("integrity: L3");
        sidecarContent.Should().Contain("reason:");  // some real flac error
    }
}
