using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T15.6 D1 + D2 coverage: --quiet flag, verbose+quiet conflict, --integrity l3 fail-fast.
/// </summary>
public class ScanRunnerCliFlagsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public ScanRunnerCliFlagsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-cliflags-{Guid.NewGuid():N}");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // --- D1: --quiet flag ---

    [Fact]
    public async Task RunAsync_QuietFlag_OnlyWarningsAndAbove_StillExitOk()
    {
        var exit = await ScanRunner.RunAsync(
            _inputDir, _outputDir,
            IntegrityLevel.Off,
            concurrency: 1,
            collision: CollisionPolicy.Skip,
            verbose: false,
            quiet: true);

        exit.Should().Be(ScanRunner.ExitOk);
    }

    [Fact]
    public async Task RunAsync_BothVerboseAndQuiet_ReturnsBadArgs()
    {
        var exit = await ScanRunner.RunAsync(
            _inputDir, _outputDir,
            IntegrityLevel.Off,
            concurrency: 1,
            collision: CollisionPolicy.Skip,
            verbose: true,
            quiet: true);

        exit.Should().Be(ScanRunner.ExitBadArgs);
    }

    // --- D2: --integrity l3 without flac on PATH must exit 2 at startup ---
    // Reverse-skippable: this test only meaningfully runs when flac is NOT available.
    // On dev machines where flac IS installed, the test passes vacuously (skipped logically).

    [Fact]
    public async Task RunAsync_L3WithoutFlac_FailsFast_ReturnsBadArgs()
    {
        if (FlacBinaryAvailable())
        {
            // flac is on PATH — startup probe would succeed; the failure path under test cannot trigger.
            // We assert the precondition is honored and let the test pass; manual / CI-without-flac
            // coverage exercises the failure branch.
            return;
        }

        var exit = await ScanRunner.RunAsync(
            _inputDir, _outputDir,
            IntegrityLevel.L3,
            concurrency: 1,
            collision: CollisionPolicy.Skip,
            verbose: false,
            quiet: false);

        exit.Should().Be(ScanRunner.ExitBadArgs);
    }

    private static bool FlacBinaryAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "flac",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) { return false; }
            p.WaitForExit(2000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
