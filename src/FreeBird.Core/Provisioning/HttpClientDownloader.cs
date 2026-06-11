using Serilog;

namespace FreeBird.Core.Provisioning;

/// <summary>
/// Default IHttpDownloader backed by HttpClient. Supports timeout, single retry on transient failure,
/// and progress reporting. Owns its HttpClient (constructed once; reused; respects User-Agent).
/// </summary>
public sealed class HttpClientDownloader : IHttpDownloader, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private const int BufferSize = 81_920;  // 80KB, matches Stream.CopyTo default
    private const string UserAgent = "FreeBird-CLI/3.1";

    private readonly HttpClient _client;
    private readonly ILogger _log;

    public HttpClientDownloader(ILogger log) : this(log, BuildClient(DefaultTimeout)) { }

    /// <summary>Test seam constructor allowing injection of a pre-configured HttpClient.</summary>
    internal HttpClientDownloader(ILogger log, HttpClient client)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    private static HttpClient BuildClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return c;
    }

    public async Task<long> DownloadAsync(Uri url, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        if (url is null) throw new ArgumentNullException(nameof(url));
        if (destination is null) throw new ArgumentNullException(nameof(destination));

        try
        {
            return await DownloadOnceAsync(url, destination, progress, ct).ConfigureAwait(false);
        }
        catch (HttpDownloadException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsTransient(ex))
        {
            _log.Warning(ex, "Download from {Url} failed with transient error; retrying once", url);
            try
            {
                return await DownloadOnceAsync(url, destination, progress, ct).ConfigureAwait(false);
            }
            catch (HttpDownloadException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex2) when (IsTransient(ex2))
            {
                throw new HttpDownloadException($"HTTP request to {url} failed after retry: {ex2.Message}", ex2);
            }
        }
    }

    private async Task<long> DownloadOnceAsync(Uri url, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpDownloadException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {url}",
                (int)response.StatusCode);
        }

        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            progress?.Report(total);
        }
        return total;
    }

    private static bool IsTransient(Exception ex)
    {
        // HttpRequestException, IOException during stream read, TaskCanceledException not from caller cancel
        return ex is HttpRequestException || ex is IOException || ex is TaskCanceledException;
    }

    public void Dispose() => _client.Dispose();
}
