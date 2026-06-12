using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T19: end-to-end test that the CLI handles an empty input directory gracefully.
/// Exercises Program → ScanRunner → ScanOrchestrator → FileProcessor (none invoked) → ScanSummary.
/// </summary>
public class ScanRunnerEmptyDirTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public ScanRunnerEmptyDirTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fb-e2e-empty-{Guid.NewGuid():N}");
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
    public async Task RunAsync_EmptyInputDir_ReturnsExitOk()
    {
        var exitCode = await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.Auto,
            concurrency: 1,
            collision: CollisionPolicy.Skip,
            verbose: false);

        exitCode.Should().Be(ScanRunner.ExitOk);
    }

    [Fact]
    public async Task RunAsync_EmptyInputDir_DoesNotCreateUnexpectedFiles()
    {
        await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.Off, // Off keeps it cheapest — we just want to verify no spurious output
            concurrency: 1,
            collision: CollisionPolicy.Skip,
            verbose: false);

        // Output dir should exist (CLI creates it) but be empty
        Directory.Exists(_outputDir).Should().BeTrue();
        Directory.GetFiles(_outputDir).Should().BeEmpty();
        // No staging/failed subdirs created when no files were processed
        Directory.GetDirectories(_outputDir).Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_NonexistentInputDir_ReturnsExitBadArgs()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist");
        var exitCode = await ScanRunner.RunAsync(
            new[] { missing }, _outputDir,
            IntegrityLevel.Auto, 1, CollisionPolicy.Skip, false);

        exitCode.Should().Be(ScanRunner.ExitBadArgs);
    }

    [Fact]
    public async Task RunAsync_OutputDirDoesNotExist_GetsCreated()
    {
        var newOutput = Path.Combine(_tempDir, "new-output-dir");
        Directory.Exists(newOutput).Should().BeFalse();

        var exitCode = await ScanRunner.RunAsync(
            new[] { _inputDir }, newOutput,
            IntegrityLevel.Auto, 1, CollisionPolicy.Skip, false);

        exitCode.Should().Be(ScanRunner.ExitOk);
        Directory.Exists(newOutput).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_NonUcFilesInDir_AreIgnored_StillExitOk()
    {
        await File.WriteAllTextAsync(Path.Combine(_inputDir, "readme.txt"), "not a uc file");
        await File.WriteAllTextAsync(Path.Combine(_inputDir, "music.mp3"), "not a uc file");

        var exitCode = await ScanRunner.RunAsync(
            new[] { _inputDir }, _outputDir,
            IntegrityLevel.Auto, 1, CollisionPolicy.Skip, false);

        exitCode.Should().Be(ScanRunner.ExitOk);
        Directory.GetFiles(_outputDir).Should().BeEmpty();
    }
}
