using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Time.Testing;

namespace FreeBird.Core.Tests.Watch;

public sealed class TokenBucketRateLimiterTests
{
    [Fact]
    public async Task Unlimited_AcquireReturnsImmediately()
    {
        // callsPerSecond == 0 means unlimited / no throttling.
        using var limiter = new TokenBucketRateLimiter(callsPerSecond: 0);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            await limiter.AcquireAsync(CancellationToken.None);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "unlimited limiter must not introduce per-call latency");
    }

    [Fact]
    public async Task Limited_RespectsRate()
    {
        // capacity == callsPerSecond == 5; burst of 5 should succeed instantly,
        // 6th should wait ~200ms (1 token / 5 per second = 200ms).
        using var limiter = new TokenBucketRateLimiter(callsPerSecond: 5);

        // Drain the initial bucket.
        for (var i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(CancellationToken.None);
        }

        var sw = Stopwatch.StartNew();
        await limiter.AcquireAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(150,
            "the 6th acquire must wait for a refill (~200ms)");
        sw.ElapsedMilliseconds.Should().BeLessThan(800,
            "the wait should not be excessive");
    }

    [Fact]
    public async Task Limited_RefillsOverTime()
    {
        var fake = new FakeTimeProvider(startDateTime: DateTimeOffset.UtcNow);
        using var limiter = new TokenBucketRateLimiter(callsPerSecond: 5, timeProvider: fake);

        // Drain the bucket.
        for (var i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(CancellationToken.None);
        }

        // Advance time by 1 second — bucket should refill to capacity (5 tokens).
        fake.Advance(TimeSpan.FromSeconds(1));

        // Five more acquires should now all complete immediately.
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(CancellationToken.None);
        }
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "after a full second of refill, 5 tokens should be available immediately");
    }

    [Fact]
    public async Task AcquireAsync_HonorsCancellation()
    {
        using var limiter = new TokenBucketRateLimiter(callsPerSecond: 1);

        // Drain the single token.
        await limiter.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var act = async () => await limiter.AcquireAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Constructor_RejectsOutOfRange()
    {
        var actNeg = () => new TokenBucketRateLimiter(callsPerSecond: -1);
        var actTooBig = () => new TokenBucketRateLimiter(callsPerSecond: 101);

        actNeg.Should().Throw<ArgumentOutOfRangeException>();
        actTooBig.Should().Throw<ArgumentOutOfRangeException>();

        // Boundary values must be accepted.
        var act0 = () => new TokenBucketRateLimiter(callsPerSecond: 0);
        var act1 = () => new TokenBucketRateLimiter(callsPerSecond: 1);
        var act100 = () => new TokenBucketRateLimiter(callsPerSecond: 100);
        act0.Should().NotThrow();
        act1.Should().NotThrow();
        act100.Should().NotThrow();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var limiter = new TokenBucketRateLimiter(callsPerSecond: 5);
        limiter.Dispose();
        limiter.Dispose(); // second call must not throw
    }
}
