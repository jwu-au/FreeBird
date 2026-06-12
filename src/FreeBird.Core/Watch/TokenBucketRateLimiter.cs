using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Watch;

/// <summary>
/// Process-wide time-based rate limiter for outbound NetEase API calls.
/// One half of the v3.4 dual-gate (the other being the per-call concurrency
/// semaphore in <see cref="IGlobalApiRateLimiter"/>). Bounded by
/// <c>--api-rate-limit</c> (default 0 = unlimited; range [0, 100]).
///
/// <para>
/// <b>Historical note:</b> <c>--api-rate-limit</c> has been parsed and plumbed
/// since v3.0 but was never actually enforced — there was no token bucket in
/// the NetEase client path. T01.5 of v3.4 introduces this type to fix that
/// silent no-op (registration into the API client comes in T02).
/// </para>
/// </summary>
public interface ITokenBucketRateLimiter
{
    /// <summary>
    /// Acquires 1 token, blocking until refill if bucket is empty.
    /// If the limiter was constructed with <c>callsPerSecond == 0</c>
    /// (unlimited), returns immediately without waiting.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// Thrown if <paramref name="ct"/> is cancelled while waiting for a refill.
    /// </exception>
    ValueTask AcquireAsync(CancellationToken ct);
}

/// <summary>
/// Standard lazy-refill token bucket. Capacity equals
/// <c>callsPerSecond</c>; refill rate is <c>callsPerSecond</c> tokens / sec.
/// Tokens are accumulated as a <see cref="double"/> based on wall-clock
/// elapsed time (via <see cref="TimeProvider"/>) so the bucket also works
/// correctly under virtual time in tests.
///
/// <para>
/// Auto-registered via the <see cref="IDependency"/> marker; the singleton
/// lifetime carve-out is enforced explicitly in <c>CoreModule</c> (T03c).
/// Thread-safe via a single internal lock — this gate is called at most a
/// few hundred times per second, so lockless engineering is unwarranted.
/// </para>
/// </summary>
public sealed class TokenBucketRateLimiter : ITokenBucketRateLimiter, IDependency, IDisposable
{
    private const int MaxCallsPerSecond = 100;

    private readonly object _lock = new();
    private readonly int _callsPerSecond;
    private readonly double _capacity;
    private readonly TimeProvider _timeProvider;
    private readonly bool _unlimited;

    private double _tokens;
    private long _lastRefillTimestamp;
    private int _disposed;

    /// <param name="callsPerSecond">
    /// 0 = unlimited (no throttling); 1..100 = tokens added per second
    /// and also the burst capacity of the bucket.
    /// </param>
    /// <param name="timeProvider">
    /// Clock source. Defaults to <see cref="TimeProvider.System"/>. Tests
    /// inject <c>FakeTimeProvider</c> to drive virtual time deterministically.
    /// </param>
    public TokenBucketRateLimiter(int callsPerSecond = 0, TimeProvider? timeProvider = null)
    {
        if (callsPerSecond < 0 || callsPerSecond > MaxCallsPerSecond)
        {
            throw new ArgumentOutOfRangeException(
                nameof(callsPerSecond),
                callsPerSecond,
                $"callsPerSecond must be in [0, {MaxCallsPerSecond}] (0 = unlimited).");
        }

        _callsPerSecond = callsPerSecond;
        _unlimited = callsPerSecond == 0;
        _capacity = callsPerSecond;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tokens = _capacity;
        _lastRefillTimestamp = _timeProvider.GetTimestamp();
    }

    public async ValueTask AcquireAsync(CancellationToken ct)
    {
        if (_unlimited)
        {
            // Honor cancellation even in the fast path so callers can rely on
            // CT propagation regardless of throttling configuration.
            ct.ThrowIfCancellationRequested();
            return;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            TimeSpan waitFor;
            lock (_lock)
            {
                RefillLocked();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return;
                }

                // Compute how long until 1 full token accrues at the refill rate.
                var deficit = 1.0 - _tokens;
                var secondsToWait = deficit / _callsPerSecond;
                waitFor = TimeSpan.FromSeconds(secondsToWait);
            }

            // Use the TimeProvider-aware Task.Delay overload so virtual time
            // (FakeTimeProvider) drives the wait in tests.
            await Task.Delay(waitFor, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds tokens accrued since the last refill, capped at <see cref="_capacity"/>.
    /// Must be called inside <see cref="_lock"/>.
    /// </summary>
    private void RefillLocked()
    {
        var now = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(_lastRefillTimestamp, now);
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var accrued = elapsed.TotalSeconds * _callsPerSecond;
        _tokens = Math.Min(_capacity, _tokens + accrued);
        _lastRefillTimestamp = now;
    }

    public void Dispose()
    {
        // Nothing to release today; field reserved for future extension and
        // to keep semantics consistent with GlobalApiRateLimiter.
        Interlocked.Exchange(ref _disposed, 1);
    }
}
