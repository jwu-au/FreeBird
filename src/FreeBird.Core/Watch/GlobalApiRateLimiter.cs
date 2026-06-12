using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Watch;

/// <summary>
/// Process-wide concurrency gate for outbound NetEase API calls.
/// One half of the v3.4 dual-gate (the other being the per-second token bucket
/// in <c>ApiRateLimit</c>). Bounded by <c>--api-concurrency</c> (default 4).
/// Acquire returns a token whose <see cref="IDisposable.Dispose"/> releases
/// the slot; double-dispose is a no-op.
/// </summary>
public interface IGlobalApiRateLimiter : IAsyncDisposable
{
    /// <summary>
    /// Acquires a concurrency slot, waiting if all slots are in use.
    /// The returned token MUST be disposed to release the slot.
    /// </summary>
    ValueTask<IDisposable> AcquireAsync(CancellationToken ct);
}

/// <summary>
/// <see cref="SemaphoreSlim"/>-backed implementation of <see cref="IGlobalApiRateLimiter"/>.
/// Auto-registered via the <see cref="IDependency"/> marker; the singleton lifetime
/// carve-out is enforced explicitly in <c>CoreModule</c> (T03c).
/// </summary>
public sealed class GlobalApiRateLimiter : IGlobalApiRateLimiter, IDependency
{
    private readonly SemaphoreSlim _semaphore;
    private int _disposed;

    public GlobalApiRateLimiter(int maxConcurrency = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_semaphore);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _sem;

        public Releaser(SemaphoreSlim sem) => _sem = sem;

        public void Dispose()
        {
            var sem = Interlocked.Exchange(ref _sem, null);
            sem?.Release();
        }
    }
}
