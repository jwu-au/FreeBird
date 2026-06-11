using System.Net;
using System.Net.Http;
using FluentAssertions;
using FreeBird.Core.Provisioning;
using Moq;
using Moq.Protected;
using Serilog;

namespace FreeBird.Core.Tests.Provisioning;

public class HttpClientDownloaderTests
{
    private static (HttpClientDownloader downloader, Mock<HttpMessageHandler> handler) Build(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns<HttpRequestMessage, CancellationToken>(respond);
        var client = new HttpClient(handler.Object);
        return (new HttpClientDownloader(new Mock<ILogger>().Object, client), handler);
    }

    [Fact]
    public async Task DownloadAsync_HappyPath_WritesBytesAndReportsTotal()
    {
        var payload = new byte[100_000];
        new Random(42).NextBytes(payload);

        var (d, _) = Build((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        }));

        await using var ms = new MemoryStream();
        var total = await d.DownloadAsync(new Uri("https://example/x.zip"), ms, null, CancellationToken.None);
        total.Should().Be(100_000);
        ms.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task DownloadAsync_Non200_ThrowsHttpDownloadException_WithStatusCode()
    {
        var (d, _) = Build((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        await using var ms = new MemoryStream();
        var act = async () => await d.DownloadAsync(new Uri("https://example/x.zip"), ms, null, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<HttpDownloadException>();
        ex.Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task DownloadAsync_HttpRequestException_RetriesOnce_ThenSucceeds()
    {
        var calls = 0;
        var (d, handler) = Build((req, ct) =>
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("transient");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[42]) });
        });

        await using var ms = new MemoryStream();
        var total = await d.DownloadAsync(new Uri("https://example/x.zip"), ms, null, CancellationToken.None);
        total.Should().Be(42);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task DownloadAsync_TransientTwice_ThrowsHttpDownloadException()
    {
        var (d, _) = Build((req, ct) => throw new HttpRequestException("network down"));
        await using var ms = new MemoryStream();
        var act = async () => await d.DownloadAsync(new Uri("https://example/x.zip"), ms, null, CancellationToken.None);
        await act.Should().ThrowAsync<HttpDownloadException>();
    }

    [Fact]
    public async Task DownloadAsync_Non200_NoRetry()
    {
        var calls = 0;
        var (d, _) = Build((req, ct) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });

        await using var ms = new MemoryStream();
        var act = async () => await d.DownloadAsync(new Uri("https://example/x.zip"), ms, null, CancellationToken.None);
        await act.Should().ThrowAsync<HttpDownloadException>();
        calls.Should().Be(1);  // HTTP failure status is not transient — no retry
    }

    private sealed class SyncProgress : IProgress<long>
    {
        public List<long> Reports { get; } = new();
        public void Report(long value) { lock (Reports) Reports.Add(value); }
    }

    [Fact]
    public async Task DownloadAsync_ReportsProgress_Cumulatively()
    {
        var payload = new byte[200_000];
        var (d, _) = Build((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var progress = new SyncProgress();

        await using var ms = new MemoryStream();
        await d.DownloadAsync(new Uri("https://example/x.zip"), ms, progress, CancellationToken.None);

        progress.Reports.Should().NotBeEmpty();
        progress.Reports.Last().Should().Be(200_000);
    }

    [Fact]
    public async Task DownloadAsync_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (d, _) = Build((req, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        await using var ms = new MemoryStream();
        var act = async () => await d.DownloadAsync(new Uri("https://example/x.zip"), ms, null, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DownloadAsync_NullUrl_Throws()
    {
        var (d, _) = Build((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var act = async () => await d.DownloadAsync(null!, new MemoryStream(), null, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("url");
    }

    [Fact]
    public async Task DownloadAsync_NullDestination_Throws()
    {
        var (d, _) = Build((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var act = async () => await d.DownloadAsync(new Uri("https://x"), null!, null, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("destination");
    }
}
