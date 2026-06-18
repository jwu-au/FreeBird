using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Metadata;
using FreeBird.Core.NetEase;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Time.Testing;
using Serilog;

namespace FreeBird.Core.Tests.NetEase;

public class NetEaseApiClientTests
{
    // -----------------------------------------------------------------------
    // Test infrastructure: capturing mock HTTP handler.
    // -----------------------------------------------------------------------
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
        public HttpRequestMessage? LastRequest { get; private set; }

        public CapturingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastRequest = req;
            return await _responder(req, ct);
        }
    }

    private static HttpClient BuildClient(CapturingHandler handler, bool withUserAgent = true)
    {
        var http = new HttpClient(handler);
        if (withUserAgent)
        {
            // Mirror T09's User-Agent so test #8 verifies the same singleton-client header shape.
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15");
        }
        return http;
    }

    private static ILogger NullLogger() => new LoggerConfiguration().CreateLogger();

    // v3.4 T02: the production NetEaseApiClient ctor now requires two limiters.
    // For these tests the limiters must be no-ops (callsPerSecond=0 = unlimited,
    // ample concurrency cap) so behavior is identical to pre-T02.
    private static ITokenBucketRateLimiter UnlimitedRate() => new TokenBucketRateLimiter(callsPerSecond: 0);
    private static IGlobalApiRateLimiter UnlimitedConcurrency() => new GlobalApiRateLimiter(maxConcurrency: 100);

    private static CapturingHandler RespondWith(HttpStatusCode status, string body)
    {
        return new CapturingHandler((req, ct) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }));
    }

    private static TimeSpan DefaultTimeout => TimeSpan.FromSeconds(5);

    // Minimal valid response JSON: one song with id 3367798042.
    private const string ValidJsonOneSong = """
        {
          "songs": [
            {
              "id": 3367798042,
              "name": "终不负",
              "artists": [
                { "id": 1, "name": "Rohith Pai Kasturi" },
                { "id": 2, "name": "IAMPRANN" }
              ],
              "album": { "id": 99, "name": "OST" }
            }
          ],
          "code": 200
        }
        """;

    private const string ValidJsonNoAlbum = """
        {
          "songs": [
            {
              "id": 3367798042,
              "name": "终不负",
              "artists": [ { "id": 1, "name": "Rohith Pai Kasturi" } ]
            }
          ],
          "code": 200
        }
        """;

    private const string EmptySongsJson = """{ "songs": [], "code": 200 }""";

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Returns_Success_OnValid200WithSongs()
    {
        var handler = RespondWith(HttpStatusCode.OK, ValidJsonOneSong);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(3367798042L, DefaultTimeout, CancellationToken.None);

        var success = result.Should().BeOfType<NetEaseApiResult.Success>().Subject;
        success.Song.MusicId.Should().Be(3367798042L);
        success.Song.Title.Should().Be("终不负");
        success.Song.Artists.Should().BeEquivalentTo(new[] { "Rohith Pai Kasturi", "IAMPRANN" });
        success.Song.Album.Should().Be("OST");
    }

    [Fact]
    public async Task Returns_NotFound_When_SongsArrayEmpty()
    {
        var handler = RespondWith(HttpStatusCode.OK, EmptySongsJson);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        var notFound = result.Should().BeOfType<NetEaseApiResult.NotFound>().Subject;
        notFound.MusicId.Should().Be(42L);
    }

    [Fact]
    public async Task Returns_NetworkError_WhenHttpRequestExceptionThrown()
    {
        var handler = new CapturingHandler((req, ct) =>
            throw new HttpRequestException("connection refused"));
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        var err = result.Should().BeOfType<NetEaseApiResult.NetworkError>().Subject;
        err.Message.Should().Contain("connection refused");
    }

    [Fact]
    public async Task Returns_Timeout_WhenTaskCancelledByLinkedToken()
    {
        var handler = new CapturingHandler(async (req, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidJsonOneSong),
            };
        });
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(
            42L,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.Timeout>();
    }

    [Fact]
    public async Task Returns_DeserializationError_OnMalformedJson()
    {
        var handler = RespondWith(HttpStatusCode.OK, "not json");
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        var err = result.Should().BeOfType<NetEaseApiResult.DeserializationError>().Subject;
        err.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Returns_Success_WithNullAlbum_WhenAlbumFieldMissing()
    {
        var handler = RespondWith(HttpStatusCode.OK, ValidJsonNoAlbum);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(3367798042L, DefaultTimeout, CancellationToken.None);

        var success = result.Should().BeOfType<NetEaseApiResult.Success>().Subject;
        success.Song.Album.Should().BeNull();
        success.Song.Title.Should().Be("终不负");
    }

    [Fact]
    public async Task RequestUri_ExactlyMatches_SpecFormat()
    {
        var handler = RespondWith(HttpStatusCode.OK, ValidJsonOneSong);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        await sut.GetSongDetailAsync(3367798042L, DefaultTimeout, CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://music.163.com/api/song/detail/?id=3367798042&ids=%5B3367798042%5D");
    }

    [Fact]
    public async Task Request_IncludesUserAgent_FromSingletonClient()
    {
        var handler = RespondWith(HttpStatusCode.OK, ValidJsonOneSong);
        var sut = new NetEaseApiClient(BuildClient(handler, withUserAgent: true), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        await sut.GetSongDetailAsync(3367798042L, DefaultTimeout, CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        var ua = handler.LastRequest!.Headers.UserAgent.ToString();
        ua.Should().Contain("Mozilla/5.0");
        ua.Should().Contain("Safari/605.1.15");
    }

    [Fact]
    public async Task Returns_PropagatesCancellation_WhenUserCancels()
    {
        // Disambiguation test: a user-cancelled token must NOT be mapped to Timeout —
        // it must propagate as OperationCanceledException to the caller.
        var handler = new CapturingHandler(async (req, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ValidJsonOneSong),
            };
        });
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        Func<Task> act = () => sut.GetSongDetailAsync(42L, DefaultTimeout, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // -----------------------------------------------------------------------
    // T4: rate-limit / risk-control classification.
    //
    // NetEase signals throttling/risk-control as HTTP 200 with body code -460
    // ("Cheating") or -447, and also via HTTP 429/403/5xx. These MUST classify
    // as NetEaseApiResult.RateLimited, distinct from a genuine NotFound (200 +
    // empty songs + code 200), so the backoff ladder picks the right schedule.
    // -----------------------------------------------------------------------

    private static CapturingHandler RespondWith(HttpStatusCode status, string body, TimeSpan? retryAfterDelta)
    {
        return new CapturingHandler((req, ct) =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (retryAfterDelta is { } delta)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
            }
            return Task.FromResult(response);
        });
    }

    private const string RateLimited460Json = """{ "code": -460, "msg": "Cheating" }""";
    private const string RateLimited447Json = """{ "code": -447 }""";
    private const string UnknownCodeJson = """{ "songs": [], "code": 250 }""";

    [Fact]
    public async Task Http200_Code460_ReturnsRateLimited()
    {
        var handler = RespondWith(HttpStatusCode.OK, RateLimited460Json);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.RateLimited>();
    }

    [Fact]
    public async Task Http200_Code447_ReturnsRateLimited()
    {
        var handler = RespondWith(HttpStatusCode.OK, RateLimited447Json);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.RateLimited>();
    }

    [Fact]
    public async Task Http200_Code200_EmptySongs_ReturnsNotFound()
    {
        // Genuine not-found (200 + code 200 + empty songs) must NOT be reclassified.
        var handler = RespondWith(HttpStatusCode.OK, EmptySongsJson);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.NotFound>();
    }

    [Fact]
    public async Task Http200_Code200_WithSong_ReturnsSuccess()
    {
        var handler = RespondWith(HttpStatusCode.OK, ValidJsonOneSong);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(3367798042L, DefaultTimeout, CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.Success>();
    }

    [Fact]
    public async Task Http200_UnknownNonzeroCode_ReturnsDeserializationError()
    {
        var handler = RespondWith(HttpStatusCode.OK, UnknownCodeJson);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        var err = result.Should().BeOfType<NetEaseApiResult.DeserializationError>().Subject;
        err.Message.Should().Contain("250");
    }

    [Fact]
    public async Task Http429_ReturnsRateLimited()
    {
        var handler = RespondWith((HttpStatusCode)429, "");
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.RateLimited>();
    }

    [Fact]
    public async Task Http403_ReturnsRateLimited()
    {
        var handler = RespondWith(HttpStatusCode.Forbidden, "");
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.RateLimited>();
    }

    [Fact]
    public async Task Http503_ReturnsRateLimited()
    {
        var handler = RespondWith(HttpStatusCode.ServiceUnavailable, "");
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.RateLimited>();
    }

    [Fact]
    public async Task Http503_HtmlBody_ReturnsRateLimited_NotDeserialize()
    {
        // M4: non-2xx + non-JSON (HTML) body must still classify by STATUS, never
        // fall through to DeserializationError.
        var handler = new CapturingHandler((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("<html><body>503 Service Unavailable</body></html>", Encoding.UTF8, "text/html"),
        }));
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        result.Should().BeOfType<NetEaseApiResult.RateLimited>();
    }

    [Fact]
    public async Task Http429_WithRetryAfterSeconds_PopulatesRetryAfter()
    {
        var handler = RespondWith((HttpStatusCode)429, "", retryAfterDelta: TimeSpan.FromSeconds(30));
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        var limited = result.Should().BeOfType<NetEaseApiResult.RateLimited>().Subject;
        limited.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Http429_WithRetryAfterHttpDate_PopulatesRetryAfter()
    {
        // Retry-After as an HTTP-date is computed against the injected clock, so the
        // result is deterministic: (date - now). We pin a FakeTimeProvider and set
        // the header 2 minutes ahead of that fixed instant.
        var now = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(now);
        var handler = new CapturingHandler((req, ct) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddMinutes(2));
            return Task.FromResult(response);
        });
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), fakeTime);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        var limited = result.Should().BeOfType<NetEaseApiResult.RateLimited>().Subject;
        limited.RetryAfter.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task Http429_RetryAfterClampedTo6h()
    {
        var handler = RespondWith((HttpStatusCode)429, "", retryAfterDelta: TimeSpan.FromSeconds(999999));
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        var limited = result.Should().BeOfType<NetEaseApiResult.RateLimited>().Subject;
        limited.RetryAfter.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task Http429_NoRetryAfter_NullRetryAfter()
    {
        var handler = RespondWith((HttpStatusCode)429, "");
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency(), TimeProvider.System);

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        var limited = result.Should().BeOfType<NetEaseApiResult.RateLimited>().Subject;
        limited.RetryAfter.Should().BeNull();
    }
}
