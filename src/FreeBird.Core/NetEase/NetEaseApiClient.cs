using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Metadata.Internal;
using Serilog;

namespace FreeBird.Core.NetEase;

/// <summary>
/// HTTP-only NetEase Cloud Music song-detail API client. Never throws for
/// transport/HTTP/JSON failures — every outcome maps to a
/// <see cref="NetEaseApiResult"/> union case. User cancellation (via <c>ct</c>)
/// is the only exception that propagates.
///
/// Test seam (v3 T20): the API base host/scheme can be overridden by setting the
/// <c>FB_NETEASE_BASEURL</c> environment variable (e.g.
/// <c>http://127.0.0.1:54321</c>) before constructing this client. The path
/// (<c>/api/song/detail/?id=...&amp;ids=...</c>) is always appended. Read at
/// construction time so each E2E test that swaps the env var gets a fresh URL.
/// Production behavior (no env var) is unchanged: defaults to
/// <c>https://music.163.com</c>.
/// </summary>
public sealed class NetEaseApiClient : INetEaseApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger _log;
    // Spec §5: both id and ids must be present; ids is URL-encoded [id].
    // Built per-instance so the FB_NETEASE_BASEURL test seam is observed at ctor time
    // (a static initializer would capture once per process and break sequential E2E
    // tests that need different stub ports).
    private readonly string _urlFormat;

    public NetEaseApiClient(HttpClient http, ILogger log)
    {
        _http = http;
        _log = log.ForContext<NetEaseApiClient>();
        _urlFormat = BuildUrlFormat();
    }

    private static string BuildUrlFormat()
    {
        var baseOverride = Environment.GetEnvironmentVariable("FB_NETEASE_BASEURL");
        var baseUrl = string.IsNullOrWhiteSpace(baseOverride)
            ? "https://music.163.com"
            : baseOverride.TrimEnd('/');
        return baseUrl + "/api/song/detail/?id={0}&ids=%5B{0}%5D";
    }

    public async Task<NetEaseApiResult> GetSongDetailAsync(long musicId, TimeSpan timeout, CancellationToken ct)
    {
        var url = string.Format(CultureInfo.InvariantCulture, _urlFormat, musicId);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();

        _log.Debug("NetEase API GET {Url}", url);

        try
        {
            using var response = await _http.GetAsync(url, linkedCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

            NetEaseResponseDto? payload;
            try
            {
                payload = JsonSerializer.Deserialize<NetEaseResponseDto>(json, JsonOpts);
            }
            catch (JsonException jx)
            {
                _log.Warning("NetEase API failure for {MusicId}: DeserializationError: {Reason}", musicId, jx.Message);
                return new NetEaseApiResult.DeserializationError(jx.Message);
            }

            if (payload?.Songs is null || payload.Songs.Count == 0)
            {
                _log.Warning("NetEase API failure for {MusicId}: NotFound (empty songs array)", musicId);
                return new NetEaseApiResult.NotFound(musicId);
            }

            var song = NetEaseDtoMapper.ToSongInfo(payload.Songs[0]);
            if (song is null)
            {
                const string reason = "DTO mapping failed (empty id or unexpected shape).";
                _log.Warning("NetEase API failure for {MusicId}: DeserializationError: {Reason}", musicId, reason);
                return new NetEaseApiResult.DeserializationError(reason);
            }

            return new NetEaseApiResult.Success(song);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // User-initiated cancellation. Surface to caller; do NOT map to Timeout.
            throw;
        }
        catch (OperationCanceledException)
        {
            // The linked CTS fired due to our per-request timeout.
            var elapsed = sw.Elapsed;
            _log.Warning("NetEase API failure for {MusicId}: Timeout after {Elapsed}", musicId, elapsed);
            return new NetEaseApiResult.Timeout(elapsed);
        }
        catch (HttpRequestException hx)
        {
            _log.Warning("NetEase API failure for {MusicId}: NetworkError: {Reason}", musicId, hx.Message);
            return new NetEaseApiResult.NetworkError(hx.Message);
        }
    }
}
