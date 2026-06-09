using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Cli.Tests.E2E;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// T17 — E2E: <c>--skip-initial-scan</c> suppresses startup decoding of pre-existing files.
///
/// Same setup as T16 (3 real-decodable .uc files pre-populated in the input dir), but
/// launches the runner with <c>SkipInitialScan = true</c> and a 30s poll interval. The
/// initial sweep is skipped; the first poll never fires during the ~3s test window;
/// therefore no output files should be produced.
///
/// Marked <c>Category=E2E</c> — see <see cref="WatchE2EHelpers"/> for rationale.
/// </summary>
[Collection("ConsoleRedirect")]
public class SkipInitialScanE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public SkipInitialScanE2ETests()
    {
        _tempDir = WatchE2EHelpers.CreateTempPath("skip-initial");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose() => WatchE2EHelpers.CleanupDir(_tempDir);

    [Fact]
    [Trait("Category", "E2E")]
    public async Task SkipInitialScan_ProducesNoOutputs_WhenFirstPollNeverFires()
    {
        // Pre-populate input with 3 real-decodable .uc files — exactly like T16.
        await TestFixtures.WriteFixtureAsUcAsync(TestFixtures.SampleMp3Path, _inputDir, "alpha.uc");
        await TestFixtures.WriteFixtureAsUcAsync(TestFixtures.SampleMp3Path, _inputDir, "bravo.uc");
        await TestFixtures.WriteFixtureAsUcAsync(TestFixtures.SampleMp3Path, _inputDir, "charlie.uc");

        // SkipInitialScan = true → initial sweep is suppressed. PollInterval = 30s → the first
        // periodic poll never fires within the test window.
        var opts = WatchE2EHelpers.MakeOptions(
            inputDir: _inputDir,
            outputDir: _outputDir,
            pollInterval: TimeSpan.FromSeconds(30),
            stabilityChecks: 1,
            skipInitialScan: true);

        // Run for ~3s, then cancel. During this window we expect NOTHING to be decoded.
        using var cts = new CancellationTokenSource();
        var runTask = WatchE2EHelpers.StartWatchAsync(opts, cts.Token);

        // Wait ~3 seconds. This is the "did anything sneak through?" window. We deliberately
        // do NOT poll for outputs — we WANT no outputs.
        await Task.Delay(TimeSpan.FromSeconds(3));

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected */ }

        // Assert: no decoded outputs at all. The output dir may contain a .freebird subtree
        // (logs etc.) but NO *.mp3 files at its root.
        var mp3Outputs = Directory.GetFiles(_outputDir, "*.mp3", SearchOption.TopDirectoryOnly);
        mp3Outputs.Should().BeEmpty(
            "SkipInitialScan should have suppressed the startup sweep, and the 30s first poll never fired");
    }
}
