using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Watch;

namespace FreeBird.Core.Tests.Watch;

public sealed class GlobalApiRateLimiterTests
{
    [Fact]
    public async Task Acquire_WhenSlotsAvailable_ReturnsImmediately()
    {
        await using var limiter = new GlobalApiRateLimiter(maxConcurrency: 2);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var token = await limiter.AcquireAsync(cts.Token);

        token.Should().NotBeNull();
    }

    [Fact]
    public async Task Acquire_WhenSaturated_BlocksUntilRelease()
    {
        await using var limiter = new GlobalApiRateLimiter(maxConcurrency: 1);

        // Take the only slot.
        var first = await limiter.AcquireAsync(CancellationToken.None);

        // Second acquire must not complete while first is held.
        var secondTask = limiter.AcquireAsync(CancellationToken.None).AsTask();
        var completedEarly = await Task.WhenAny(secondTask, Task.Delay(100));
        completedEarly.Should().NotBe(secondTask, "second acquire must block while slot is held");

        // Release first; second should now complete promptly.
        first.Dispose();
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        second.Dispose();
    }

    [Fact]
    public async Task Acquire_HonorsCancellation()
    {
        await using var limiter = new GlobalApiRateLimiter(maxConcurrency: 1);

        // Saturate.
        using var held = await limiter.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await limiter.AcquireAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Dispose_ReleasesUnderlyingSemaphore()
    {
        await using var limiter = new GlobalApiRateLimiter(maxConcurrency: 1);
        var token = await limiter.AcquireAsync(CancellationToken.None);

        // Releasing the token returns the slot to the semaphore.
        token.Dispose();

        // We should be able to acquire again immediately.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        using var again = await limiter.AcquireAsync(cts.Token);
        again.Should().NotBeNull();
    }

    [Fact]
    public async Task LimiterDispose_IsIdempotent()
    {
        var limiter = new GlobalApiRateLimiter(maxConcurrency: 1);
        await limiter.DisposeAsync();
        await limiter.DisposeAsync(); // second call must not throw
    }

    [Fact]
    public async Task Token_DoubleDispose_DoesNotOverRelease()
    {
        await using var limiter = new GlobalApiRateLimiter(maxConcurrency: 1);
        var token = await limiter.AcquireAsync(CancellationToken.None);

        token.Dispose();
        token.Dispose(); // must be idempotent

        // Slot count is still 1, not 2 — confirm by saturating + verifying second blocks.
        using var held = await limiter.AcquireAsync(CancellationToken.None);
        var secondTask = limiter.AcquireAsync(CancellationToken.None).AsTask();
        var winner = await Task.WhenAny(secondTask, Task.Delay(100));
        winner.Should().NotBe(secondTask, "double-dispose must not inflate slot count");
    }

    [Fact]
    public async Task MultipleAcquires_RespectMaxConcurrency()
    {
        const int Max = 4;
        await using var limiter = new GlobalApiRateLimiter(maxConcurrency: Max);

        // Acquire Max slots simultaneously — all must succeed promptly.
        var tokens = new IDisposable[Max];
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            for (var i = 0; i < Max; i++)
            {
                tokens[i] = await limiter.AcquireAsync(cts.Token);
            }
        }

        // The (Max+1)th must block.
        var extraTask = limiter.AcquireAsync(CancellationToken.None).AsTask();
        var winner = await Task.WhenAny(extraTask, Task.Delay(100));
        winner.Should().NotBe(extraTask, $"{Max + 1}th acquire must block when {Max} are held");

        // Release one and the extra should unblock.
        tokens[0].Dispose();
        var extra = await extraTask.WaitAsync(TimeSpan.FromSeconds(2));
        extra.Dispose();

        for (var i = 1; i < Max; i++)
        {
            tokens[i].Dispose();
        }
    }

    [Fact]
    public void Constructor_RejectsNonPositiveMaxConcurrency()
    {
        var act0 = () => new GlobalApiRateLimiter(maxConcurrency: 0);
        var actNeg = () => new GlobalApiRateLimiter(maxConcurrency: -1);

        act0.Should().Throw<ArgumentOutOfRangeException>();
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }
}
