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
/// T15 — E2E: when a pre-existing sidecar's (size, mtime) does NOT match the current source
/// (because the user replaced or modified the file), the watcher MUST re-decode. This proves
/// <c>FilesystemSkipDecider</c>'s source-changed path correctly re-invokes
/// <c>FileProcessor</c>.
///
/// Flow:
///   1. Write <c>input/foo.uc</c> with 4 KB random bytes.
///   2. Stat it; pre-create <c>output/.freebird-failed/foo.flac.txt</c> with matching size+mtime.
///   3. Mutate foo.uc: append 1 byte (size now 4097) AND bump its mtime forward by 2s.
///   4. Launch the runner (1s poll, 1 stability check, skip-initial-scan) and wait ~4s.
///   5. Cancel.
///   6. Assert: a sidecar exists at <c>foo.*.txt</c> whose <c>source_size</c> reflects the
///      NEW size (4097, not 4096), proving the file was re-processed (not skipped).
///
/// Marked <c>Category=E2E</c> — see <see cref="WatchE2EHelpers"/> for rationale.
/// </summary>
[Collection("ConsoleRedirect")]
public class SourceChangedRetryE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    public SourceChangedRetryE2ETests()
    {
        _tempDir = WatchE2EHelpers.CreateTempPath("source-changed");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose() => WatchE2EHelpers.CleanupDir(_tempDir);

    [Fact]
    [Trait("Category", "E2E")]
    public async Task PreExistingSidecar_WithStaleSizeAndMtime_CausesRedecode()
    {
        // 1) 4 KB of random bytes.
        var srcPath = Path.Combine(_inputDir, "foo.uc");
        var rnd = new Random(5678);
        var initialBytes = new byte[4 * 1024];
        rnd.NextBytes(initialBytes);
        File.WriteAllBytes(srcPath, initialBytes);

        // 2) Stat the ORIGINAL state and pre-create a sidecar that matches it exactly.
        var origInfo = new FileInfo(srcPath);
        var origSize = origInfo.Length;
        var origMtime = new DateTimeOffset(origInfo.LastWriteTimeUtc, TimeSpan.Zero);

        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        Directory.CreateDirectory(failedDir);
        var origSidecarPath = Path.Combine(failedDir, "foo.flac.txt");
        File.WriteAllText(origSidecarPath, BuildSidecarText(
            sourcePath: srcPath,
            format: "Flac",
            integrity: "L3",
            reason: "pre-existing sidecar from earlier failure (test fixture)",
            sourceSize: origSize,
            sourceMtime: origMtime));

        // 3) MUTATE foo.uc: append 1 byte (size: 4096 -> 4097) AND bump mtime forward by 2s.
        File.AppendAllText(srcPath, "X");
        var newMtimeUtc = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        File.SetLastWriteTimeUtc(srcPath, newMtimeUtc);

        var newInfo = new FileInfo(srcPath);
        newInfo.Length.Should().Be(origSize + 1, "we appended exactly one byte");
        newInfo.LastWriteTimeUtc.Should().NotBe(origInfo.LastWriteTimeUtc, "we bumped mtime");

        // 4) Launch the runner.
        var opts = WatchE2EHelpers.MakeOptions(
            inputDir: _inputDir,
            outputDir: _outputDir,
            pollInterval: TimeSpan.FromSeconds(1),
            stabilityChecks: 1,
            skipInitialScan: true);

        using var cts = new CancellationTokenSource();
        var runTask = WatchE2EHelpers.StartWatchAsync(opts, cts.Token);

        // 5) Wait ~4s — enough for several poll cycles + reprocessing.
        await Task.Delay(TimeSpan.FromSeconds(4));

        cts.Cancel();
        try { await runTask; } catch (OperationCanceledException) { /* expected */ }

        // 6) Assert: a sidecar for foo.* exists whose source_size reflects the NEW size.
        // Random bytes will likely sniff as Unknown -> quarantine writes foo.bin.txt. The
        // original foo.flac.txt may also remain in place (different filename). What matters
        // is that AT LEAST ONE sidecar reports the new size.
        var sidecars = Directory.GetFiles(failedDir, "foo.*.txt");
        sidecars.Should().NotBeEmpty("at least the pre-existing sidecar should still be there");

        var newSize = origSize + 1;
        var anyReportsNewSize = false;
        foreach (var sidecar in sidecars)
        {
            var content = File.ReadAllText(sidecar);
            if (content.Contains($"source_size: {newSize.ToString(CultureInfo.InvariantCulture)}"))
            {
                anyReportsNewSize = true;
                break;
            }
        }

        anyReportsNewSize.Should().BeTrue(
            "after the source changed, FileProcessor should have been re-invoked and written a sidecar " +
            $"whose source_size reflects the new size ({newSize} bytes), proving the skip was correctly bypassed");
    }

    /// <summary>
    /// Build a 7-field v2 sidecar matching the format <c>FileProcessor.BuildSidecarContent</c>
    /// emits.
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
