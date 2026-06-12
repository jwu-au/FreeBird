using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Metadata;
using FreeBird.Core.NetEase;
using FreeBird.Core.Watch;
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
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency());

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
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency());

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        var notFound = result.Should().BeOfType<NetEaseApiResult.NotFound>().Subject;
        notFound.MusicId.Should().Be(42L);
    }

    [Fact]
    public async Task Returns_NetworkError_WhenHttpRequestExceptionThrown()
    {
        var handler = new CapturingHandler((req, ct) =>
            throw new HttpRequestException("connection refused"));
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency());

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
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency());

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
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency());

        var result = await sut.GetSongDetailAsync(42L, DefaultTimeout, CancellationToken.None);

        var err = result.Should().BeOfType<NetEaseApiResult.DeserializationError>().Subject;
        err.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Returns_Success_WithNullAlbum_WhenAlbumFieldMissing()
    {
        var handler = RespondWith(HttpStatusCode.OK, ValidJsonNoAlbum);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency());

        var result = await sut.GetSongDetailAsync(3367798042L, DefaultTimeout, CancellationToken.None);

        var success = result.Should().BeOfType<NetEaseApiResult.Success>().Subject;
        success.Song.Album.Should().BeNull();
        success.Song.Title.Should().Be("终不负");
    }

    [Fact]
    public async Task RequestUri_ExactlyMatches_SpecFormat()
    {
        var handler = RespondWith(HttpStatusCode.OK, ValidJsonOneSong);
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency());

        await sut.GetSongDetailAsync(3367798042L, DefaultTimeout, CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be("https://music.163.com/api/song/detail/?id=3367798042&ids=%5B3367798042%5D");
    }

    [Fact]
    public async Task Request_IncludesUserAgent_FromSingletonClient()
    {
        var handler = RespondWith(HttpStatusCode.OK, ValidJsonOneSong);
        var sut = new NetEaseApiClient(BuildClient(handler, withUserAgent: true), NullLogger(), UnlimitedRate(), UnlimitedConcurrency());

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
        var sut = new NetEaseApiClient(BuildClient(handler), NullLogger(), UnlimitedRate(), UnlimitedConcurrency());

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        Func<Task> act = () => sut.GetSongDetailAsync(42L, DefaultTimeout, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
