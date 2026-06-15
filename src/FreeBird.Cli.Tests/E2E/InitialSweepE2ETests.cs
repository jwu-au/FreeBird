using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Cli.Tests;
using FreeBird.Cli.Tests.E2E;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// T16 — E2E: initial sweep on startup decodes pre-existing .uc files in the input dir.
///
/// Pre-populates the input with three real XOR-encrypted .uc files (built from a real MP3
/// fixture, so they will actually decode), launches the real <see cref="WatchRunner"/>
/// with a long poll interval (30s) so only the initial sweep runs during the test
/// window, waits a few seconds for the sweep to complete, then cancels.
///
/// Marked <c>Category=E2E</c> because it drives the real orchestrator and is wall-clock
/// slow. Use <c>dotnet test --filter "Category!=E2E"</c> for fast inner-loop runs.
/// </summary>
[Collection("GlobalStaticState")]
public class InitialSweepE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;
    // T13: stem-based output names ("alpha.mp3", "bravo.mp3", "charlie.mp3") —
    // pin StemBasedFileNamer instead of v3 default MetadataAwareFileNamer.
    private readonly StemNamerTestOverride _namerOverride = new();

    public InitialSweepE2ETests()
    {
        _tempDir = WatchE2EHelpers.CreateTempPath("initial-sweep");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        _namerOverride.Dispose();
        WatchE2EHelpers.CleanupDir(_tempDir);
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task InitialSweep_DecodesAllPreExistingUcFiles()
    {
        // Pre-populate input with 3 real-decodable .uc files (each XOR-encrypted MP3 fixture).
        await TestFixtures.WriteFixtureAsUcAsync(TestFixtures.SampleMp3Path, _inputDir, "alpha.uc");
        await TestFixtures.WriteFixtureAsUcAsync(TestFixtures.SampleMp3Path, _inputDir, "bravo.uc");
        await TestFixtures.WriteFixtureAsUcAsync(TestFixtures.SampleMp3Path, _inputDir, "charlie.uc");

        // Long poll interval (30s) + stability=1 so the first poll never fires during the
        // ~3s test window — only the initial sweep gets to run. stability=1 lets the
        // sweep promote files immediately (no second size-check pass needed).
        var opts = WatchE2EHelpers.MakeOptions(
            inputDir: _inputDir,
            outputDir: _outputDir,
            pollInterval: TimeSpan.FromSeconds(30),
            stabilityChecks: 1);

        // 6s overall budget: 3s for the sweep to complete + slack for graceful shutdown.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var runTask = WatchE2EHelpers.StartWatchAsync(opts, cts.Token);

        var alphaOut = Path.Combine(_outputDir, "alpha.mp3");
        var bravoOut = Path.Combine(_outputDir, "bravo.mp3");
        var charlieOut = Path.Combine(_outputDir, "charlie.mp3");

        // Wait for the three outputs to materialize (up to 4s). We poll for the slowest
        // (charlie) — if it shows up, all three did.
        var allDecoded = await WatchE2EHelpers.WaitForAsync(
            () => File.Exists(alphaOut) && File.Exists(bravoOut) && File.Exists(charlieOut),
            TimeSpan.FromSeconds(4));

        // Signal the runner to wind down, then wait for it.
        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected */ }

        allDecoded.Should().BeTrue("initial sweep should have decoded all 3 pre-existing .uc files");
        File.Exists(alphaOut).Should().BeTrue();
        File.Exists(bravoOut).Should().BeTrue();
        File.Exists(charlieOut).Should().BeTrue();
    }
}
