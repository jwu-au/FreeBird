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
/// T20 — E2E: cleanup UX. If a user deletes a successfully-decoded output file, the next
/// poll cycle should re-decode it (because the input .uc is still present and there's no
/// success sidecar making us skip it).
///
/// Flow:
///   1. Pre-populate input with 1 real-decodable .uc file.
///   2. Launch the runner with a short (1s) poll interval and initial sweep ON.
///   3. Wait for the output to appear (initial sweep).
///   4. Record its mtime, delete it.
///   5. Wait for the output to reappear on the next poll cycle.
///   6. Assert mtime is strictly greater than the original.
///
/// Marked <c>Category=E2E</c> — see <see cref="WatchE2EHelpers"/> for rationale.
/// </summary>
[Collection("ConsoleRedirect")]
public class CleanupUxE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;
    // T13: stem-based output name ("song.mp3") — pin StemBasedFileNamer.
    private readonly StemNamerTestOverride _namerOverride = new();

    public CleanupUxE2ETests()
    {
        _tempDir = WatchE2EHelpers.CreateTempPath("cleanup-ux");
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
    public async Task DeletingOutput_CausesRedecode_OnNextPollCycle()
    {
        await TestFixtures.WriteFixtureAsUcAsync(TestFixtures.SampleMp3Path, _inputDir, "song.uc");

        // Short poll interval (1s) so the next cycle fires within the test window.
        // stabilityChecks=1 keeps the size-stability gate from delaying promotion.
        var opts = WatchE2EHelpers.MakeOptions(
            inputDir: _inputDir,
            outputDir: _outputDir,
            pollInterval: TimeSpan.FromSeconds(1),
            stabilityChecks: 1,
            skipInitialScan: false);

        using var cts = new CancellationTokenSource();
        var runTask = WatchE2EHelpers.StartWatchAsync(opts, cts.Token);

        var outPath = Path.Combine(_outputDir, "song.mp3");

        // 1) Wait up to 5s for initial sweep to produce the output.
        var initiallyAppeared = await WatchE2EHelpers.WaitForFileAsync(outPath, TimeSpan.FromSeconds(5));
        initiallyAppeared.Should().BeTrue("initial sweep should produce song.mp3");

        // 2) Record original mtime, then delete the output.
        var originalMtime = File.GetLastWriteTimeUtc(outPath);
        File.Delete(outPath);
        File.Exists(outPath).Should().BeFalse("we just deleted it");

        // 3) Wait up to 4s for the next poll cycle to redecode. Mtime resolution on macOS
        // APFS is sub-millisecond; on some Linux/Windows filesystems it's 1-2s, so we ALSO
        // wait until the mtime strictly advances past the original (handles same-second
        // filesystem timestamp granularity).
        var redecoded = await WatchE2EHelpers.WaitForAsync(
            () => File.Exists(outPath) && File.GetLastWriteTimeUtc(outPath) > originalMtime,
            TimeSpan.FromSeconds(4));

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected */ }

        redecoded.Should().BeTrue(
            "after deletion, the next poll cycle should redecode and the new mtime should exceed the original");
        File.Exists(outPath).Should().BeTrue();
        File.GetLastWriteTimeUtc(outPath).Should().BeAfter(originalMtime);
    }
}
