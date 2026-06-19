using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Metadata;
using FreeBird.Core.NetEase;
using FreeBird.Core.Watch;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.NetEase;

/// <summary>
/// T02 (v3.4): verifies <see cref="NetEaseApiClient"/> wires both the
/// process-wide token bucket and concurrency semaphore around every outbound
/// HTTP call, in the documented order: rate bucket FIRST (bounded wait),
/// then concurrency semaphore (unbounded wait, held for the duration of the
/// HTTP request).
/// </summary>
public sealed class NetEaseApiClientDualGateTests
{
    // -----------------------------------------------------------------------
    // Test infrastructure mirrors NetEaseApiClientTests (intentional duplication
    // for test isolation: this fixture also tracks gate-call ordering and
    // in-flight HTTP concurrency, which the original fixture does not).
    // -----------------------------------------------------------------------

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public CapturingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => _responder(req, ct);
    }

    private static HttpClient BuildClient(HttpMessageHandler handler) => new(handler);

    private static ILogger NullLogger() => new LoggerConfiguration().CreateLogger();

    private const string ValidJsonOneSong = """
        {
          "songs": [
            {
              "id": 3367798042,
              "name": "song-name",
              "artists": [ { "id": 1, "name": "A" } ],
              "album": { "id": 99, "name": "OST" }
            }
          ],
          "code": 200
        }
        """;

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(ValidJsonOneSong, Encoding.UTF8, "application/json"),
    };

    // -----------------------------------------------------------------------
    // 1) Call-order assertion: rate bucket BEFORE semaphore.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSongDetail_AcquiresRateBucket_BeforeSemaphore()
    {
        var calls = new List<string>();
        var callsLock = new object();

        var rate = new Mock<ITokenBucketRateLimiter>(MockBehavior.Strict);
        rate.Setup(r => r.AcquireAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                lock (callsLock) { calls.Add("rate-acquire"); }
                return ValueTask.CompletedTask;
            });

        var slot = new Mock<IDisposable>();
        slot.Setup(s => s.Dispose()).Callback(() =>
        {
            lock (callsLock) { calls.Add("sem-release"); }
        });

        var sem = new Mock<IGlobalApiRateLimiter>(MockBehavior.Strict);
        sem.Setup(s => s.AcquireAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                lock (callsLock) { calls.Add("sem-acquire"); }
                return ValueTask.FromResult(slot.Object);
            });

        var handler = new CapturingHandler((_, _) =>
        {
            lock (callsLock) { calls.Add("http-send"); }
            return Task.FromResult(Ok());
        });

        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), rate.Object, sem.Object, TimeProvider.System);

        var result = await sut.GetSongDetailAsync(
            3367798042L,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.Success>();
        calls.Should().ContainInOrder("rate-acquire", "sem-acquire", "http-send", "sem-release");
    }

    // -----------------------------------------------------------------------
    // 2) Semaphore slot is released AFTER the HTTP response completes
    //    (and after JSON deserialization), not before.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSongDetail_ReleasesSemaphore_AfterHttpCompletes()
    {
        var httpCompleted = false;
        var slotDisposedWhileHttpInFlight = false;
        var disposeCalled = false;

        var rate = Mock.Of<ITokenBucketRateLimiter>(r =>
            r.AcquireAsync(It.IsAny<CancellationToken>()) == ValueTask.CompletedTask);

        var slot = new Mock<IDisposable>();
        slot.Setup(s => s.Dispose()).Callback(() =>
        {
            disposeCalled = true;
            if (!httpCompleted)
            {
                slotDisposedWhileHttpInFlight = true;
            }
        });

        var sem = Mock.Of<IGlobalApiRateLimiter>(s =>
            s.AcquireAsync(It.IsAny<CancellationToken>()) == ValueTask.FromResult(slot.Object));

        var handler = new CapturingHandler(async (_, ct) =>
        {
            // Simulate non-zero HTTP work duration.
            await Task.Delay(50, ct);
            httpCompleted = true;
            return Ok();
        });

        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), rate, sem, TimeProvider.System);

        var result = await sut.GetSongDetailAsync(
            42L,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.Success>();
        disposeCalled.Should().BeTrue("the concurrency slot must be released after the call");
        slotDisposedWhileHttpInFlight.Should().BeFalse(
            "the slot must remain held for the full duration of the HTTP request");
    }

    // -----------------------------------------------------------------------
    // 3) Cancellation during rate-bucket wait surfaces OCE; semaphore is
    //    never touched (so no slot is leaked).
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSongDetail_HonorsCancellation_DuringRateAcquire()
    {
        var semAcquireCount = 0;

        var rate = new Mock<ITokenBucketRateLimiter>(MockBehavior.Strict);
        rate.Setup(r => r.AcquireAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async ct =>
            {
                // Wait forever on the user's CT — until cancelled.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        var sem = new Mock<IGlobalApiRateLimiter>(MockBehavior.Strict);
        sem.Setup(s => s.AcquireAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref semAcquireCount);
                return ValueTask.FromResult<IDisposable>(Mock.Of<IDisposable>());
            });

        var handler = new CapturingHandler((_, _) => Task.FromResult(Ok()));

        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), rate.Object, sem.Object, TimeProvider.System);

        using var cts = new CancellationTokenSource();
        var task = sut.GetSongDetailAsync(42L, TimeSpan.FromSeconds(30), cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        Func<Task> act = () => task;
        await act.Should().ThrowAsync<OperationCanceledException>();

        semAcquireCount.Should().Be(0,
            "semaphore must not be acquired if rate-bucket wait is cancelled");
    }

    // -----------------------------------------------------------------------
    // 4) Cancellation during semaphore wait surfaces OCE; the rate token
    //    has already been consumed but the HTTP request is never sent.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSongDetail_HonorsCancellation_DuringSemaphoreAcquire()
    {
        var httpSent = false;

        var rate = Mock.Of<ITokenBucketRateLimiter>(r =>
            r.AcquireAsync(It.IsAny<CancellationToken>()) == ValueTask.CompletedTask);

        var sem = new Mock<IGlobalApiRateLimiter>(MockBehavior.Strict);
        sem.Setup(s => s.AcquireAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async ct =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Mock.Of<IDisposable>(); // unreachable
            });

        var handler = new CapturingHandler((_, _) =>
        {
            httpSent = true;
            return Task.FromResult(Ok());
        });

        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), rate, sem.Object, TimeProvider.System);

        using var cts = new CancellationTokenSource();
        var task = sut.GetSongDetailAsync(42L, TimeSpan.FromSeconds(30), cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        Func<Task> act = () => task;
        await act.Should().ThrowAsync<OperationCanceledException>();

        httpSent.Should().BeFalse("HTTP must not fire if semaphore acquire is cancelled");
    }

    // -----------------------------------------------------------------------
    // 5) Per-request timeout fires while waiting for a rate token: result
    //    must be NetEaseApiResult.Timeout (not propagated OCE) because the
    //    linked CTS — not the user's CT — fired.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSongDetail_TimeoutDuringRateWait_ReturnsTimeoutResult()
    {
        // DETERMINISTIC: a mocked rate limiter whose AcquireAsync blocks until the
        // passed-in token is cancelled, then surfaces OperationCanceledException.
        // The client's per-request timeout (linked CTS, 100ms) is what cancels that
        // token, so the wait is broken by the TIMEOUT, not by wall-clock token refill.
        // This removes the previous real-clock race (a real rate=1 limiter occasionally
        // refilled within the 100ms window on fast/loaded Windows runners, letting the
        // request succeed before the timeout fired -> flaky Success instead of Timeout).
        var rate = new Mock<ITokenBucketRateLimiter>();
        rate.Setup(r => r.AcquireAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async token =>
            {
                // Wait forever until the (timeout-linked) token is cancelled.
                var tcs = new TaskCompletionSource();
                using var reg = token.Register(() => tcs.TrySetResult());
                await tcs.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            });

        // The concurrency gate is never reached (the rate gate wait is cancelled by the
        // timeout first), but provide a benign slot so the mock is well-formed.
        var sem = Mock.Of<IGlobalApiRateLimiter>(s =>
            s.AcquireAsync(It.IsAny<CancellationToken>())
                == ValueTask.FromResult<IDisposable>(Mock.Of<IDisposable>()));

        var handler = new CapturingHandler((_, _) => Task.FromResult(Ok()));

        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), rate.Object, sem, TimeProvider.System);

        var result = await sut.GetSongDetailAsync(
            42L,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.Timeout>();
    }

    // -----------------------------------------------------------------------
    // 6) Unlimited rate + ample concurrency: 50 parallel requests all succeed
    //    quickly (sanity check — no spurious throttling).
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSongDetail_BothGatesUnlimited_NoThrottling()
    {
        using var rate = new TokenBucketRateLimiter(callsPerSecond: 0); // unlimited
        await using var sem = new GlobalApiRateLimiter(maxConcurrency: 100);

        var handler = new CapturingHandler((_, _) => Task.FromResult(Ok()));
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), rate, sem, TimeProvider.System);

        var tasks = new Task<NetEaseApiResult>[50];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = sut.GetSongDetailAsync(3367798042L + i, TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().BeOfType<NetEaseApiResult.Success>());
    }

    // -----------------------------------------------------------------------
    // 7) High concurrency demand vs. semaphore cap: only N in-flight at any
    //    instant. Verified with an Interlocked counter + observed maximum.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task GetSongDetail_HighConcurrency_RespectsSemaphoreCap()
    {
        const int Cap = 2;
        const int Requests = 10;

        using var rate = new TokenBucketRateLimiter(callsPerSecond: 0); // no rate throttle
        await using var sem = new GlobalApiRateLimiter(maxConcurrency: Cap);

        var inFlight = 0;
        var observedMax = 0;

        var handler = new CapturingHandler(async (_, ct) =>
        {
            var current = Interlocked.Increment(ref inFlight);
            // Atomic-max via CAS loop on observedMax.
            int snapshot;
            do
            {
                snapshot = observedMax;
                if (current <= snapshot) { break; }
            } while (Interlocked.CompareExchange(ref observedMax, current, snapshot) != snapshot);

            try
            {
                await Task.Delay(30, ct);
                return Ok();
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
            }
        });

        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), rate, sem, TimeProvider.System);

        var tasks = new Task<NetEaseApiResult>[Requests];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = sut.GetSongDetailAsync(3367798042L + i, TimeSpan.FromSeconds(10), CancellationToken.None);
        }

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().BeOfType<NetEaseApiResult.Success>());
        observedMax.Should().BeLessThanOrEqualTo(Cap,
            "no more than {0} concurrent in-flight HTTP calls should ever be observed", Cap);
        observedMax.Should().BeGreaterThan(0,
            "at least one request should have been in flight (sanity check)");
    }
}
