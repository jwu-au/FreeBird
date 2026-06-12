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
}
