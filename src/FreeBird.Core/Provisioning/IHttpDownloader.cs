namespace FreeBird.Core.Provisioning;

/// <summary>
/// Downloads bytes from a URL. Thin abstraction over HttpClient to enable unit testing
/// of consumers (e.g., WindowsFlacAutoInstaller) without HttpMessageHandler mocking.
/// </summary>
public interface IHttpDownloader
{
    /// <summary>
    /// Download the resource at the given URL to a stream. Throws HttpDownloadException on failure.
    /// </summary>
    /// <param name="url">Absolute URL (typically https://).</param>
    /// <param name="destination">Stream to write bytes into. Caller owns lifetime.</param>
    /// <param name="progress">Optional progress reporter (cumulative bytes downloaded).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Total bytes downloaded.</returns>
    Task<long> DownloadAsync(Uri url, Stream destination, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>Thrown when an HTTP download fails (network, non-success status, etc).</summary>
public sealed class HttpDownloadException : Exception
{
    public int? StatusCode { get; }

    public HttpDownloadException(string message) : base(message) { }
    public HttpDownloadException(string message, Exception inner) : base(message, inner) { }
    public HttpDownloadException(string message, int statusCode) : base(message)
    {
        StatusCode = statusCode;
    }
}
