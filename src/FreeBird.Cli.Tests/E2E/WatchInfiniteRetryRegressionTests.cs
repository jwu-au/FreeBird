using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli;
using FreeBird.Core.Models;

namespace FreeBird.Cli.Tests.E2E;

/// <summary>
/// v3.2 T05 — E2E regression: an integrity-failed .uc! file must be quarantined on the
/// first watch poll and skipped on the second poll. Before T02, the IntegrityFailed
/// quarantine path named the sidecar by musicId (not source stem), so
/// <see cref="FreeBird.Core.Watch.FilesystemSkipDecider"/>'s stem-glob couldn't find it
/// — the file was re-decoded and re-quarantined on every poll cycle. T02 fixed the
/// naming; T03 added a unit-level regression. THIS test proves the fix end-to-end
/// through the real watch composition (Autofac DI, real FileProcessor, real
/// TagLibIntegrityChecker, real FilesystemSkipDecider).
///
/// Flow:
///   1. Write input/&lt;stem&gt;.uc! whose XOR-decrypted plaintext is "fLaC" magic + 32 bytes
///      of junk. Sniffer returns Flac; TagLib# throws CorruptFileException on parse;
///      FileProcessor quarantines under .freebird-failed/&lt;stem&gt;.flac (+ .flac.txt sidecar).
///   2. Launch WatchRunner (real Autofac composition) with Integrity=L1, skipInitialScan,
///      1s poll, 1 stability check. Wait ~4s — enough for ≥2 poll cycles.
///   3. Capture quarantine file + sidecar timestamps and sizes; cancel the runner.
///   4. Launch the runner AGAIN against the same dirs. Wait ~4s.
///   5. ASSERT: quarantine file + sidecar still exist, with the EXACT SAME timestamps
///      and sizes — proving the skip decider matched the stem-named sidecar and the
///      file was not re-processed.
///
/// Marked Category=E2E — see <see cref="WatchE2EHelpers"/> for rationale (wall-clock slow).
/// </summary>
[Collection("RunnerOverride")]
public class WatchInfiniteRetryRegressionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _inputDir;
    private readonly string _outputDir;

    // Realistic NetEase cache filename — composite stem with musicId + bitrate + md5.
    // Pre-T02, FileProcessor would quarantine this as
    // ".freebird-failed/3367798042.flac" (musicId-only), which the stem-glob
    // ".freebird-failed/<stem>.*.txt" would NOT match → infinite re-processing.
    // Post-T02, it quarantines as ".freebird-failed/<stem>.flac" → glob matches → skip.
    private const string SourceStem =
        "3367798042-_-_5999-_-_a38658b6e504b7520bb4c507db13b9d2";
    private const string SourceFileName = SourceStem + ".uc!";

    public WatchInfiniteRetryRegressionTests()
    {
        _tempDir = WatchE2EHelpers.CreateTempPath("infinite-retry");
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose() => WatchE2EHelpers.CleanupDir(_tempDir);

    [Fact]
    [Trait("Category", "E2E")]
    public async Task Watch_IntegrityFailedFile_QuarantinedOnFirstPoll_SkippedOnSecondPoll()
    {
        // 1) Build a .uc! whose XOR-decrypted plaintext is "fLaC" magic + 32 bytes of junk
        //    bytes. The sniffer matches FLAC on the magic; TagLib# then throws
        //    CorruptFileException (no STREAMINFO block, no frames) → IntegrityResult.Failed.
        //    File size must clear MinFileSize (we use 0L below to be unambiguous).
        var ucPath = Path.Combine(_inputDir, SourceFileName);
        WriteCorruptFlacAsUc(ucPath);
        new FileInfo(ucPath).Length.Should().BeGreaterThan(0, "fixture sanity check");

        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        var expectedQuarantine = Path.Combine(failedDir, SourceStem + ".flac");
        var expectedSidecar = Path.Combine(failedDir, SourceStem + ".flac.txt");

        // 2) Build WatchOptions with L1 integrity (forces the structural check to run
        //    and fail on our corrupt FLAC). We can't use WatchE2EHelpers.MakeOptions
        //    because it hard-codes Integrity=Off; we mirror its other knobs.
        var opts = MakeL1Options(_inputDir, _outputDir, pollInterval: TimeSpan.FromSeconds(1));

        // First watch session — should quarantine the file.
        using (var cts1 = new CancellationTokenSource())
        {
            var runTask = WatchE2EHelpers.StartWatchAsync(opts, cts1.Token);

            // Wait for the quarantine file to appear (≤6s). Polling beats a flat sleep
            // — the file appears as soon as the first poll cycle processes it.
            var appeared = await WatchE2EHelpers.WaitForFileAsync(
                expectedQuarantine, TimeSpan.FromSeconds(6));
            appeared.Should().BeTrue(
                $"after the first watch cycle, {expectedQuarantine} should exist " +
                "because the corrupt FLAC fails L1 integrity → IntegrityFailed quarantine " +
                "(named by source stem per v3.2 T02)");

            // Also wait for the sidecar — written by the same QuarantineFile call but
            // separately on disk. Brief poll handles the race.
            var sidecarAppeared = await WatchE2EHelpers.WaitForFileAsync(
                expectedSidecar, TimeSpan.FromSeconds(2));
            sidecarAppeared.Should().BeTrue(
                $"sidecar {expectedSidecar} should be written alongside the quarantined file");

            cts1.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }

        // 3) Capture quarantine state after the first session.
        var quarantineInfoBefore = new FileInfo(expectedQuarantine);
        var sidecarInfoBefore = new FileInfo(expectedSidecar);
        var quarantineMtimeBefore = quarantineInfoBefore.LastWriteTimeUtc;
        var quarantineSizeBefore = quarantineInfoBefore.Length;
        var sidecarMtimeBefore = sidecarInfoBefore.LastWriteTimeUtc;
        var sidecarSizeBefore = sidecarInfoBefore.Length;

        // 4) Second watch session against the SAME dirs (source .uc! still in place).
        //    Pre-T02 this would have re-decoded, re-quarantined, and bumped the mtime.
        //    Post-T02 the stem-named sidecar matches the skip decider's glob → Skip.
        using (var cts2 = new CancellationTokenSource())
        {
            var runTask = WatchE2EHelpers.StartWatchAsync(opts, cts2.Token);

            // Wait long enough for ≥2 poll cycles to have observed the file.
            // (No file-appearance event to poll for here — we're proving NOTHING happens.)
            await Task.Delay(TimeSpan.FromSeconds(4));

            cts2.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }

        // 5) Assert: quarantine file + sidecar still present, EXACT SAME size + mtime.
        File.Exists(expectedQuarantine).Should().BeTrue(
            "second poll should skip — not delete or re-quarantine — the file");
        File.Exists(expectedSidecar).Should().BeTrue(
            "second poll should skip — sidecar must remain in place");

        var quarantineInfoAfter = new FileInfo(expectedQuarantine);
        var sidecarInfoAfter = new FileInfo(expectedSidecar);

        quarantineInfoAfter.Length.Should().Be(quarantineSizeBefore,
            "skip should not rewrite the quarantined file");
        quarantineInfoAfter.LastWriteTimeUtc.Should().Be(quarantineMtimeBefore,
            "skip should not touch the quarantined file's mtime (proves no re-quarantine)");

        sidecarInfoAfter.Length.Should().Be(sidecarSizeBefore,
            "skip should not rewrite the sidecar");
        sidecarInfoAfter.LastWriteTimeUtc.Should().Be(sidecarMtimeBefore,
            "skip should not touch the sidecar's mtime (proves SourceUnchangedSinceFailure fired)");

        // No additional sidecars or quarantine files for this stem.
        var allStemFiles = Directory.GetFiles(failedDir, SourceStem + ".*");
        allStemFiles.Should().HaveCount(2,
            $"expected exactly 2 files (quarantine + sidecar) for stem {SourceStem}, " +
            $"found {allStemFiles.Length}: [{string.Join(", ", allStemFiles.Select(Path.GetFileName))}]");

        // No decoded output landed in the output dir top-level.
        var decodedOutputs = Directory.GetFiles(_outputDir, "*", SearchOption.TopDirectoryOnly);
        decodedOutputs.Should().BeEmpty(
            "the file must remain quarantined — nothing should land in the success output path");
    }

    /// <summary>
    /// Build WatchOptions that mirror <see cref="WatchE2EHelpers.MakeOptions"/> but with
    /// <see cref="IntegrityLevel.L1"/> so the structural check runs against the decoded
    /// fake FLAC and fails. MinFileSize=0 so our tiny fixture isn't filtered upstream.
    /// </summary>
    private static WatchOptions MakeL1Options(string inputDir, string outputDir, TimeSpan pollInterval)
    {
        return new WatchOptions
        {
            InputDirs = new[] { inputDir },
            OutputDir = outputDir,
            Integrity = IntegrityLevel.L1,
            Concurrency = 2,
            Collision = CollisionPolicy.Overwrite,
            PollInterval = pollInterval,
            StabilityChecks = 1,
            MinFileSize = 0L,
            SkipInitialScan = true,
            NoLogFile = true,
            Verbose = false,
            Quiet = true,
            Offline = true,  // avoid network calls; not exercised on the IntegrityFailed path anyway
        };
    }

    /// <summary>
    /// Write a .uc! file whose XOR-decrypted plaintext (key 0xA3, self-inverse) is the
    /// FLAC magic "fLaC" followed by 32 bytes of 0xFF — enough to sniff as FLAC and
    /// trigger <see cref="TagLib.CorruptFileException"/> in the L1 integrity checker
    /// (no STREAMINFO block, no frames).
    /// </summary>
    private static void WriteCorruptFlacAsUc(string ucPath)
    {
        // Plaintext: "fLaC" (0x66 0x4C 0x61 0x43) + 32 bytes of 0xFF.
        var plaintext = new byte[4 + 32];
        plaintext[0] = 0x66;
        plaintext[1] = 0x4C;
        plaintext[2] = 0x61;
        plaintext[3] = 0x43;
        for (int i = 4; i < plaintext.Length; i++)
        {
            plaintext[i] = 0xFF;
        }

        // XOR-encrypt with 0xA3 (Netease cache scheme — self-inverse).
        var encrypted = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++)
        {
            encrypted[i] = (byte)(plaintext[i] ^ 0xA3);
        }

        File.WriteAllBytes(ucPath, encrypted);
    }
}
