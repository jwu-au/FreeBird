using System;
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
        // callsPerSecond == 0 means unlimited / no throttling. Deterministic: with a
        // FakeTimeProvider that is NEVER advanced, every acquire must still complete.
        // If the unlimited fast-path were broken (i.e. it tried to wait for a refill),
        // these acquires would block forever on virtual time and the test would hang.
        var fake = new FakeTimeProvider(startDateTime: DateTimeOffset.UtcNow);
        using var limiter = new TokenBucketRateLimiter(callsPerSecond: 0, timeProvider: fake);

        for (var i = 0; i < 1000; i++)
        {
            var acquire = limiter.AcquireAsync(CancellationToken.None);
            acquire.IsCompleted.Should().BeTrue(
                "an unlimited limiter must complete synchronously without consulting the clock");
            await acquire;
        }
    }

    [Fact]
    public async Task Limited_RespectsRate()
    {
        // capacity == callsPerSecond == 5; a burst of 5 succeeds instantly, the 6th must
        // wait for a refill. Deterministic: drive virtual time so the 6th acquire cannot
        // complete until exactly the refill interval (1 token / 5 cps = 200ms) elapses.
        var fake = new FakeTimeProvider(startDateTime: DateTimeOffset.UtcNow);
        using var limiter = new TokenBucketRateLimiter(callsPerSecond: 5, timeProvider: fake);

        // Drain the initial bucket (these complete synchronously — tokens are available).
        for (var i = 0; i < 5; i++)
        {
            var burst = limiter.AcquireAsync(CancellationToken.None);
            burst.IsCompleted.Should().BeTrue("the initial burst of 5 tokens is available immediately");
            await burst;
        }

        // The 6th acquire must block until a token refills.
        var sixth = limiter.AcquireAsync(CancellationToken.None).AsTask();
        // Give the limiter a chance to schedule its virtual-time delay, then assert it
        // is still waiting because no virtual time has passed.
        await Task.Yield();
        sixth.IsCompleted.Should().BeFalse("no time has elapsed, so the 6th token has not refilled");

        // Just under the refill interval: still blocked.
        fake.Advance(TimeSpan.FromMilliseconds(199));
        await Task.Yield();
        sixth.IsCompleted.Should().BeFalse("199ms < the 200ms refill interval, so the token is not yet available");

        // Reaching the full refill interval releases the 6th acquire.
        fake.Advance(TimeSpan.FromMilliseconds(1));
        await sixth.WaitAsync(TimeSpan.FromSeconds(5));
        sixth.IsCompletedSuccessfully.Should().BeTrue("after a full 200ms refill the 6th acquire completes");
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

        // Five more acquires should now all complete synchronously (tokens available).
        for (var i = 0; i < 5; i++)
        {
            var acquire = limiter.AcquireAsync(CancellationToken.None);
            acquire.IsCompleted.Should().BeTrue(
                "after a full second of refill, all 5 tokens are available immediately");
            await acquire;
        }
    }

    [Fact]
    public async Task AcquireAsync_HonorsCancellation()
    {
        var fake = new FakeTimeProvider(startDateTime: DateTimeOffset.UtcNow);
        using var limiter = new TokenBucketRateLimiter(callsPerSecond: 1, timeProvider: fake);

        // Drain the single token.
        await limiter.AcquireAsync(CancellationToken.None);

        // An already-cancelled token must surface OperationCanceledException deterministically,
        // with no reliance on wall-clock timing.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

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
