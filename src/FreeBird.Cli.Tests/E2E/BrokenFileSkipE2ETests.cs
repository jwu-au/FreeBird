using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Cli.Tests.E2E;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// T14 — E2E: when a sidecar in <c>.freebird-failed/</c> records the same (size, mtime) as
/// the current source, the watcher MUST skip the file. This proves
/// <c>FilesystemSkipDecider</c>'s <c>SourceUnchangedSinceFailure</c> short-circuit works.
///
/// Flow:
///   1. Write <c>input/foo.uc</c> with 4 KB of random bytes (will not sniff as a known format).
///   2. Pre-create <c>output/.freebird-failed/foo.flac.txt</c> with all 7 sidecar fields,
///      with <c>source_size</c> and <c>source_mtime</c> matching foo.uc's stat values exactly.
///   3. Launch the runner (1s poll, 1 stability check, skip-initial-scan) and wait ~3.5s
///      so polling has a chance to observe foo.uc.
///   4. Cancel.
///   5. Assert: the original sidecar file is unchanged (same path, same mtime), no new
///      sidecar with a different extension was created, and no decoded output exists.
///
/// Marked <c>Category=E2E</c> — see <see cref="WatchE2EHelpers"/> for rationale.
/// </summary>
[Collection("ConsoleRedirect")]
public class BrokenFileSkipE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public BrokenFileSkipE2ETests()
    {
        _tempDir = WatchE2EHelpers.CreateTempPath("broken-skip");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose() => WatchE2EHelpers.CleanupDir(_tempDir);

    [Fact]
    [Trait("Category", "E2E")]
    public async Task PreExistingSidecar_WithMatchingSizeAndMtime_CausesSkip()
    {
        // 1) 4 KB of random bytes — won't sniff as any known audio format.
        var srcPath = Path.Combine(_inputDir, "foo.uc");
        var rnd = new Random(1234);
        var bytes = new byte[4 * 1024];
        rnd.NextBytes(bytes);
        File.WriteAllBytes(srcPath, bytes);

        // 2) Stat foo.uc, then pre-create a sidecar with EXACTLY matching size + mtime.
        var srcInfo = new FileInfo(srcPath);
        var srcSize = srcInfo.Length;
        var srcMtime = new DateTimeOffset(srcInfo.LastWriteTimeUtc, TimeSpan.Zero);

        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        Directory.CreateDirectory(failedDir);
        var sidecarPath = Path.Combine(failedDir, "foo.flac.txt");
        File.WriteAllText(sidecarPath, BuildSidecarText(
            sourcePath: srcPath,
            format: "Flac",
            integrity: "L3",
            reason: "pre-existing sidecar from earlier failure (test fixture)",
            sourceSize: srcSize,
            sourceMtime: srcMtime));

        // Capture the sidecar's own mtime BEFORE launching the runner so we can prove the
        // file wasn't rewritten.
        var sidecarMtimeBefore = File.GetLastWriteTimeUtc(sidecarPath);

        // 3) Launch the runner: 1s poll, 1 stability check, skip the initial sweep so the
        // file is only observed via polling (which exercises the skip decider).
        var opts = WatchE2EHelpers.MakeOptions(
            inputDir: _inputDir,
            outputDir: _outputDir,
            pollInterval: TimeSpan.FromSeconds(1),
            stabilityChecks: 1,
            skipInitialScan: true);

        using var cts = new CancellationTokenSource();
        var runTask = WatchE2EHelpers.StartWatchAsync(opts, cts.Token);

        // 4) Wait ~3.5s — enough for ~3 poll cycles to fire.
        await Task.Delay(TimeSpan.FromMilliseconds(3500));

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected */ }

        // 5) Assert: sidecar untouched.
        File.Exists(sidecarPath).Should().BeTrue(
            "the pre-existing sidecar must remain in place — skip should not delete or rewrite it");

        var sidecarMtimeAfter = File.GetLastWriteTimeUtc(sidecarPath);
        sidecarMtimeAfter.Should().Be(sidecarMtimeBefore,
            "the pre-existing sidecar should not have been rewritten by the runner");

        // No new sidecar with a different extension was created.
        var allSidecars = Directory.GetFiles(failedDir, "foo.*.txt");
        allSidecars.Should().HaveCount(1, "no new sidecar with a different extension should be created");
        allSidecars[0].Should().Be(sidecarPath);

        // No decoded output exists.
        var outputs = Directory.GetFiles(_outputDir, "foo.*", SearchOption.TopDirectoryOnly);
        outputs.Should().BeEmpty("the source should have been skipped, not decoded");
    }

    /// <summary>
    /// Build a 7-field v2 sidecar matching the format <c>FileProcessor.BuildSidecarContent</c>
    /// emits. Critically, <c>source_mtime</c> is formatted with <c>"O"</c> roundtrip format so
    /// <c>TextSidecarReader</c> can parse it back to the exact same <see cref="DateTimeOffset"/>.
    /// </summary>
    private static string BuildSidecarText(
        string sourcePath,
        string format,
        string integrity,
        string reason,
        long sourceSize,
        DateTimeOffset sourceMtime)
    {
        var sb = new StringBuilder();
        sb.Append("timestamp: ").AppendLine(DateTime.UtcNow.ToString("O"));
        sb.Append("source:    ").AppendLine(sourcePath);
        sb.Append("format:    ").AppendLine(format);
        sb.Append("integrity: ").AppendLine(integrity);
        sb.Append("reason:    ").AppendLine(reason);
        sb.Append("source_size: ").AppendLine(sourceSize.ToString(CultureInfo.InvariantCulture));
        sb.Append("source_mtime: ").AppendLine(sourceMtime.ToString("O", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
