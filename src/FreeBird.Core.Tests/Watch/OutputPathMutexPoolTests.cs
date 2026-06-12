using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Watch;

namespace FreeBird.Core.Tests.Watch;

/// <summary>
/// Tests for <see cref="OutputPathMutexPool"/> — per-output-path refcount-managed
/// mutex pool used by the v3.4 multi-input watch to prevent two WatchTasks from
/// writing the same output file simultaneously.
/// </summary>
public sealed class OutputPathMutexPoolTests
{
    [Fact]
    public async Task Acquire_DifferentPaths_DoNotBlockEachOther()
    {
        using var pool = new OutputPathMutexPool();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Two distinct paths -> two distinct mutexes -> both acquired immediately.
        using var tokenA = await pool.AcquireAsync("/tmp/fb-test-a.flac", cts.Token);
        using var tokenB = await pool.AcquireAsync("/tmp/fb-test-b.flac", cts.Token);

        pool.EntryCount.Should().Be(2,
            "two distinct paths should produce two live entries");
    }

    [Fact]
    public async Task Acquire_SamePath_SecondBlocksUntilRelease()
    {
        using var pool = new OutputPathMutexPool();
        const string path = "/tmp/fb-test-shared.flac";

        var firstToken = await pool.AcquireAsync(path, CancellationToken.None);

        // Start a second acquire on the SAME path; it must NOT complete yet.
        using var blockCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var secondTask = pool.AcquireAsync(path, blockCts.Token).AsTask();

        // Give the second acquire a moment to attempt; it should remain blocked.
        var completed = await Task.WhenAny(secondTask, Task.Delay(150));
        completed.Should().NotBe(secondTask,
            "the second acquire on the same path must block while the first holder is alive");

        pool.EntryCount.Should().Be(1,
            "the same key should produce one shared entry regardless of refcount");

        // Release the first holder; the second should now proceed.
        firstToken.Dispose();
        var secondToken = await secondTask;
        secondToken.Should().NotBeNull();
        secondToken.Dispose();

        pool.EntryCount.Should().Be(0,
            "after both holders release, the entry should be evicted");
    }

    [Fact]
    public async Task EntryCount_GrowsAndShrinks()
    {
        using var pool = new OutputPathMutexPool();

        var tA = await pool.AcquireAsync("/tmp/x1", CancellationToken.None);
        var tB = await pool.AcquireAsync("/tmp/x2", CancellationToken.None);
        var tC = await pool.AcquireAsync("/tmp/x3", CancellationToken.None);

        pool.EntryCount.Should().Be(3);

        tA.Dispose();
        pool.EntryCount.Should().Be(2);

        tB.Dispose();
        pool.EntryCount.Should().Be(1);

        tC.Dispose();
        pool.EntryCount.Should().Be(0);
    }

    [Fact]
    public async Task Canonicalization_TreatsTrailingSlashAsSame()
    {
        using var pool = new OutputPathMutexPool();

        // Path.GetFullPath normalizes trailing separators; both should map to one key.
        using var t1 = await pool.AcquireAsync("/tmp/fb-canon-test", CancellationToken.None);

        pool.EntryCount.Should().Be(1);

        // Verify the SECOND acquire would block (proves same key) — use a short timeout.
        using var ctsBlock = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var act = async () => await pool.AcquireAsync("/tmp/fb-canon-test/", ctsBlock.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "trailing-slash variant should canonicalize to the same key and therefore block");

        pool.EntryCount.Should().Be(1,
            "the blocked acquire must clean up its refcount on cancellation");
    }

    [SkippableFact]
    public async Task Canonicalization_OnWindowsOrMac_TreatsCaseAsSame()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
            "Case-insensitive canonicalization is only applied on Windows/macOS.");

        using var pool = new OutputPathMutexPool();

        using var t1 = await pool.AcquireAsync("/tmp/FB-Case-Test", CancellationToken.None);
        pool.EntryCount.Should().Be(1);

        // Second acquire with different case should block on the same key.
        using var ctsBlock = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var act = async () => await pool.AcquireAsync("/tmp/fb-case-test", ctsBlock.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        pool.EntryCount.Should().Be(1,
            "case-variant paths should collapse to a single entry on case-insensitive OSes");
    }

    [SkippableFact]
    public async Task Canonicalization_OnLinux_TreatsCaseAsDistinct()
    {
        Skip.IfNot(OperatingSystem.IsLinux(),
            "Linux ext4 is case-sensitive; case-folding must NOT be applied.");

        using var pool = new OutputPathMutexPool();

        using var t1 = await pool.AcquireAsync("/tmp/FB-Case-Test", CancellationToken.None);
        using var t2 = await pool.AcquireAsync("/tmp/fb-case-test", CancellationToken.None);

        pool.EntryCount.Should().Be(2,
            "on Linux, case-variant paths are distinct files and so must be distinct keys");
    }

    // --- T03b: deeper cross-OS canonicalization matrix --------------------------
    // These tests exercise edge cases the T03 implementation already supports
    // (drive-letter case-folding on Windows, dot-segment resolution, relative-
    // path resolution, NFC/NFD intentional non-normalization on macOS, and
    // case-sensitive Linux behaviour) but T03 itself only smoke-tested. They
    // are pure additions — no production change accompanies them.

    [SkippableFact]
    public async Task Canonicalization_OnWindows_DriveLetterCase_TreatsAsSame()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "Drive letters are a Windows-only concept; NTFS is case-insensitive.");

        using var pool = new OutputPathMutexPool();

        // Path.GetFullPath preserves drive-letter case as written, but the
        // Windows case-fold step in Canonicalize (ToUpperInvariant) collapses
        // them onto a single key — so both acquires share the same entry and
        // the second one must block on the same semaphore.
        var firstToken = await pool.AcquireAsync(@"C:\temp\x", CancellationToken.None);
        pool.EntryCount.Should().Be(1);

        using var ctsBlock = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var act = async () => await pool.AcquireAsync(@"c:\temp\x", ctsBlock.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "drive-letter case-variant must canonicalize to the same key and therefore block");

        pool.EntryCount.Should().Be(1,
            "case-variant drive letters should collapse to one entry on Windows");

        firstToken.Dispose();
    }

    [SkippableFact]
    public async Task Canonicalization_OnMac_NFC_vs_NFD_TreatsAsDistinct()
    {
        Skip.IfNot(OperatingSystem.IsMacOS(),
            "APFS (default since macOS 10.13) stores filenames as-written without "
            + "Unicode normalization; HFS+ used to normalize to NFD but is now legacy.");

        using var pool = new OutputPathMutexPool();

        // NFC form: precomposed 'é' = U+00E9.
        var nfc = "/tmp/fb-unicode-\u00e9";
        // NFD form: 'e' (U+0065) + combining acute (U+0301), rendered identically.
        var nfd = "/tmp/fb-unicode-e\u0301";

        using var t1 = await pool.AcquireAsync(nfc, CancellationToken.None);
        using var t2 = await pool.AcquireAsync(nfd, CancellationToken.None);

        // Our Canonicalize does NOT NFC/NFD-fold deliberately: APFS treats these
        // as distinct directory entries (`ls` would show two files), so collapsing
        // them in the mutex pool would falsely serialize writes to *different*
        // files. HFS+ would have collapsed them to NFD at the filesystem layer,
        // but HFS+ is no longer the macOS default and we follow APFS semantics.
        pool.EntryCount.Should().Be(2,
            "APFS treats NFC and NFD as distinct files; the pool must not collapse them");
    }

    [SkippableFact]
    public async Task Canonicalization_OnLinux_TrailingSlashTrimmed_StillCollapses()
    {
        Skip.IfNot(OperatingSystem.IsLinux(),
            "Asserts both trailing-slash trim AND case-sensitivity on a single OS.");

        using var pool = new OutputPathMutexPool();

        // (a) trailing-slash trim is OS-independent (driven by Path.GetFullPath
        //     plus our trim) — verify it on Linux explicitly.
        using var t1 = await pool.AcquireAsync("/tmp/fb-linux-canon", CancellationToken.None);
        pool.EntryCount.Should().Be(1);

        using var ctsBlock = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var act = async () => await pool.AcquireAsync("/tmp/fb-linux-canon/", ctsBlock.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "trailing-slash variant should canonicalize to the same key on Linux too");

        pool.EntryCount.Should().Be(1);

        // (b) but case differences MUST remain distinct keys on Linux.
        using var t2 = await pool.AcquireAsync("/tmp/fb-LINUX-canon/", CancellationToken.None);
        pool.EntryCount.Should().Be(2,
            "case-variant paths are distinct files on Linux even after trailing-slash trim");
    }

    [Fact]
    public async Task Canonicalization_RelativePath_ResolvedAgainstCwd()
    {
        using var pool = new OutputPathMutexPool();

        // Path.GetFullPath resolves a relative path against the current working
        // directory, so "./fb-rel-test" and "<cwd>/fb-rel-test" must map to the
        // same canonical key.
        var relative = Path.Combine(".", "fb-rel-test");
        var absolute = Path.Combine(Directory.GetCurrentDirectory(), "fb-rel-test");

        using var t1 = await pool.AcquireAsync(relative, CancellationToken.None);
        pool.EntryCount.Should().Be(1);

        using var ctsBlock = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var act = async () => await pool.AcquireAsync(absolute, ctsBlock.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "relative path should resolve to the same absolute key as the equivalent absolute form");

        pool.EntryCount.Should().Be(1,
            "relative and absolute spellings of the same file must share one entry");
    }

    [Fact]
    public async Task Canonicalization_DotDotComponents_Resolved()
    {
        using var pool = new OutputPathMutexPool();

        // Path.GetFullPath collapses ".." components, so these two spellings
        // refer to the same file and must share a single pool entry. Use a
        // path rooted at the current directory so the test works on every OS
        // (Windows doesn't have "/tmp", macOS may /private-prefix it, etc.).
        var root = Directory.GetCurrentDirectory();
        var withDotDot = Path.Combine(root, "a", "..", "fb-dotdot-test");
        var direct = Path.Combine(root, "fb-dotdot-test");

        using var t1 = await pool.AcquireAsync(withDotDot, CancellationToken.None);
        pool.EntryCount.Should().Be(1);

        using var ctsBlock = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var act = async () => await pool.AcquireAsync(direct, ctsBlock.Token);
        await act.Should().ThrowAsync<OperationCanceledException>(
            "Path.GetFullPath resolves '..' segments, so both spellings must map to the same key");

        pool.EntryCount.Should().Be(1);
    }

    [Fact]
    public async Task Acquire_HonorsCancellation_BeforeSemaphoreWait()
    {
        using var pool = new OutputPathMutexPool();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await pool.AcquireAsync("/tmp/fb-precancel", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        pool.EntryCount.Should().Be(0,
            "a pre-cancelled acquire must not leak a pool entry");
    }

    [Fact]
    public async Task Acquire_HonorsCancellation_DuringSemaphoreWait()
    {
        using var pool = new OutputPathMutexPool();
        const string path = "/tmp/fb-during-cancel";

        // First holder owns the only slot.
        using var firstToken = await pool.AcquireAsync(path, CancellationToken.None);

        // Second acquire will block; we cancel mid-wait.
        using var cts = new CancellationTokenSource();
        var secondTask = pool.AcquireAsync(path, cts.Token).AsTask();

        // Confirm it's actually blocked, then cancel.
        var ready = await Task.WhenAny(secondTask, Task.Delay(100));
        ready.Should().NotBe(secondTask, "second acquire must block before we cancel");

        cts.Cancel();

        var act = async () => await secondTask;
        await act.Should().ThrowAsync<OperationCanceledException>();

        pool.EntryCount.Should().Be(1,
            "the cancelled waiter must release its refcount; only the first holder's entry remains");
    }

    [Fact]
    public async Task Stress_50Threads_NoDeadlockNoLeak()
    {
        using var pool = new OutputPathMutexPool();

        // 5 paths, rotated so the same key is heavily contended.
        var paths = Enumerable.Range(0, 5).Select(i => $"/tmp/fb-stress-{i}").ToArray();

        const int workers = 50;
        const int iterationsPerWorker = 100;

        var sw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, workers).Select(workerId => Task.Run(async () =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                var key = paths[(workerId + i) % paths.Length];
                using var token = await pool.AcquireAsync(key, CancellationToken.None);
                // Minimal "critical section" — just yield to encourage interleaving.
                await Task.Yield();
            }
        })).ToArray();

        var allDone = Task.WhenAll(tasks);
        var winner = await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromSeconds(5)));
        winner.Should().Be(allDone,
            "stress test must complete within 5 seconds (no deadlock)");

        // Surface any per-task exceptions.
        await allDone;

        sw.Stop();

        pool.EntryCount.Should().Be(0,
            "after all 5000 acquire/release pairs complete, no entries should remain");
    }

    [Fact]
    public async Task Token_DoubleDispose_DoesNotOverRelease()
    {
        using var pool = new OutputPathMutexPool();
        const string path = "/tmp/fb-double-dispose";

        var token = await pool.AcquireAsync(path, CancellationToken.None);

        token.Dispose();
        var act = () => token.Dispose();
        act.Should().NotThrow("double-dispose must be a no-op");

        pool.EntryCount.Should().Be(0,
            "double-dispose must not over-release or corrupt refcount state");

        // Sanity: pool is still usable after a double-dispose round-trip.
        using var t2 = await pool.AcquireAsync(path, CancellationToken.None);
        pool.EntryCount.Should().Be(1);
    }
    [Fact]
    public async Task Token_DisposeAfterPoolDispose_DoesNotThrow()
    {
        // I-1 regression: previously, Pool.Dispose() disposed all entry semaphores,
        // then Token.Dispose() called entry.Sem.Release() on a disposed semaphore
        // and threw ObjectDisposedException. The pool-disposal path now no-ops the
        // token release so try/using/await-using patterns in test teardown are safe
        // regardless of disposal order.
        var pool = new OutputPathMutexPool();
        var token = await pool.AcquireAsync("/tmp/fb-pool-dispose-first", CancellationToken.None);

        pool.Dispose();

        var act = () => token.Dispose();
        act.Should().NotThrow(
            "Token.Dispose() after Pool.Dispose() must be a safe no-op, not throw ObjectDisposedException");
    }

    [Fact]
    public async Task Acquire_EmptyString_ThrowsArgumentException()
    {
        // I-2: empty string is no more a valid output path than null is. Catch it
        // at the boundary with a clear ArgumentException rather than producing a
        // canonicalized "" key that quietly serialises unrelated callers.
        using var pool = new OutputPathMutexPool();

        var act = async () => await pool.AcquireAsync(string.Empty, CancellationToken.None);

        // Must surface from the explicit boundary check with paramName="path",
        // not from a downstream Path.GetFullPath("") with paramName="path" /
        // message "Value cannot be null." that masks the real input problem.
        var ex = await act.Should().ThrowAsync<ArgumentException>();
        ex.And.ParamName.Should().Be("path",
            "the boundary guard must name the offending parameter so callers can fix their input");
    }
}

