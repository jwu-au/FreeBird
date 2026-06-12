using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Cli.Tests.E2E;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// T13 — E2E: size-stability detection. A file that is still being written (growing in size
/// across observations) MUST NOT be decoded by the watcher. This proves that the completion
/// detector inside <see cref="WatchRunner"/> correctly defers processing until the file's
/// size has been stable for <c>--stability-checks</c> consecutive polls.
///
/// Scenario:
///   1. Spawn a background writer that appends 100 KB to <c>input/growing.uc</c> every 2s
///      for 6 chunks (~12s total writing window).
///   2. Launch the runner with a 1s poll interval, 2 stability checks, and
///      <c>--skip-initial-scan</c> so the growing file is observed only via polling.
///   3. During the writing window, repeatedly assert that <c>output/growing.*</c> does NOT
///      exist — every poll must observe a size delta and defer.
///   4. After writing finishes, wait for the stability window to clear, then assert that the
///      file was eventually processed (either an output file OR a sidecar in
///      <c>.freebird-failed/</c>, because random bytes will not sniff as a known format).
///
/// Wall-clock budget: ~17s. This is the slowest E2E test in the suite. Acceptable.
///
/// Marked <c>Category=E2E</c> — see <see cref="WatchE2EHelpers"/> for rationale.
/// </summary>
[Collection("RunnerOverride")]
public class SizeStabilityE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public SizeStabilityE2ETests()
    {
        _tempDir = WatchE2EHelpers.CreateTempPath("size-stability");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose() => WatchE2EHelpers.CleanupDir(_tempDir);

    [Fact]
    [Trait("Category", "E2E")]
    public async Task GrowingFile_IsNotDecoded_UntilSizeStabilizes()
    {
        var growingPath = Path.Combine(_inputDir, "growing.uc");

        // Background writer: 6 chunks of 100 KB each, every 2s (~12s window).
        using var writerCts = new CancellationTokenSource();
        var writerTask = Task.Run(async () =>
        {
            var rnd = new Random(42);
            var chunk = new byte[100 * 1024];
            for (var i = 0; i < 6; i++)
            {
                if (writerCts.IsCancellationRequested) { return; }
                rnd.NextBytes(chunk);
                using (var fs = new FileStream(growingPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    await fs.WriteAsync(chunk, writerCts.Token);
                    await fs.FlushAsync(writerCts.Token);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(2), writerCts.Token); }
                catch (OperationCanceledException) { return; }
            }
        }, writerCts.Token);

        // Give the writer time to create the file (first chunk) before we start the runner —
        // otherwise the runner's first poll may not see it at all.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        var opts = WatchE2EHelpers.MakeOptions(
            inputDir: _inputDir,
            outputDir: _outputDir,
            pollInterval: TimeSpan.FromSeconds(1),
            stabilityChecks: 2,
            skipInitialScan: true);

        // Overall budget: 12s writing + 5s post-writing settle window + slack.
        using var runnerCts = new CancellationTokenSource(TimeSpan.FromSeconds(22));
        var runTask = WatchE2EHelpers.StartWatchAsync(opts, runnerCts.Token);

        // During the writing window (~first 10s), assert repeatedly that NO output appears.
        // We probe every 500ms; if any output file shows up while the file is still growing,
        // the size-stability gate is broken.
        var probeDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < probeDeadline)
        {
            var outputs = Directory.GetFiles(_outputDir, "growing.*", SearchOption.TopDirectoryOnly);
            outputs.Should().BeEmpty(
                "size-stability detection should defer processing while the file is still growing");
            await Task.Delay(500);
        }

        // Let the writer finish (it should be near done at ~12s) and the stability window clear.
        try { await writerTask; } catch (OperationCanceledException) { /* expected on shutdown */ }

        // After writing stops, wait (stabilityChecks + 1) * pollInterval + processing buffer.
        // = (2 + 1) * 1s + ~2s = ~5s for the next stable observation + decode/quarantine.
        var processedDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(7);
        var processed = false;
        while (DateTimeOffset.UtcNow < processedDeadline)
        {
            var hasOutput = Directory.GetFiles(_outputDir, "growing.*", SearchOption.TopDirectoryOnly).Length > 0;
            var failedDir = Path.Combine(_outputDir, ".freebird-failed");
            var hasSidecar = Directory.Exists(failedDir)
                && Directory.GetFiles(failedDir, "growing.*.txt").Length > 0;
            if (hasOutput || hasSidecar) { processed = true; break; }
            await Task.Delay(200);
        }

        runnerCts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected */ }

        processed.Should().BeTrue(
            "once the file stops growing, the next stable observation should let the runner process it " +
            "(producing either a decoded output or a quarantine sidecar for random-bytes content)");
    }
}
