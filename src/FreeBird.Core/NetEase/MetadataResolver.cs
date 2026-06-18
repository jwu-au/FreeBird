using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Naming;
using Serilog;

namespace FreeBird.Core.NetEase;

/// <summary>
/// Orchestrates one metadata lookup for a decoded source: honors --offline,
/// extracts musicId from the .uc/.uc! filename, calls the NetEase API, and maps
/// every <see cref="NetEaseApiResult"/> variant to a <see cref="MetadataResolution"/>
/// for the file-namer pipeline. Never throws — errors are surfaced as Fallback.
/// </summary>
public sealed class MetadataResolver : IMetadataResolver
{
    private readonly INetEaseApiClient _api;
    private readonly ILogger _log;

    public MetadataResolver(INetEaseApiClient api, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(log);
        _api = api;
        _log = log.ForContext<MetadataResolver>();
    }

    public async Task<MetadataResolution> ResolveAsync(string sourcePath, IMetadataOptions options, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);

        if (options.Offline)
        {
            _log.Debug("Offline mode: skipping API for {Path}", sourcePath);
            return new MetadataResolution.Fallback("offline-mode");
        }

        if (!MusicIdExtractor.TryExtract(sourcePath, out var musicId))
        {
            _log.Warning("Cannot extract musicId from source path {Path}; falling back", sourcePath);
            return new MetadataResolution.Fallback("metadata-empty");
        }

        var timeout = TimeSpan.FromSeconds(options.ApiTimeoutSeconds);
        var apiResult = await _api.GetSongDetailAsync(musicId, timeout, ct).ConfigureAwait(false);

        return apiResult switch
        {
            NetEaseApiResult.Success s => Pass(musicId, s.Song),
            NetEaseApiResult.NotFound => Empty(musicId),
            NetEaseApiResult.Timeout t => Failed(musicId, $"timeout after {t.Elapsed}"),
            NetEaseApiResult.NetworkError n => Failed(musicId, $"network error: {n.Message}"),
            NetEaseApiResult.DeserializationError d => Broken(musicId, d.Message),
            // T5: plumb r.RetryAfter through For(...) end-to-end. For now the
            // server Retry-After lives in the union case and is dropped here.
            NetEaseApiResult.RateLimited r => RateLimited(musicId),
            _ => throw new InvalidOperationException($"Unknown NetEaseApiResult variant: {apiResult.GetType().Name}"),
        };

        MetadataResolution.Success Pass(long id, SongInfo s)
        {
            _log.Debug("Metadata resolved for {MusicId}: {Title}", id, s.Title);
            return new MetadataResolution.Success(s);
        }

        MetadataResolution.Fallback Empty(long id)
        {
            _log.Information("Metadata not found for {MusicId}; using fallback naming", id);
            return new MetadataResolution.Fallback("metadata-empty");
        }

        MetadataResolution.Fallback Failed(long id, string reason)
        {
            _log.Warning("Metadata fetch failed for {MusicId}: {Reason}", id, reason);
            return new MetadataResolution.Fallback("metadata-fetch-failed");
        }

        MetadataResolution.Fallback Broken(long id, string reason)
        {
            _log.Error("NetEase response could not be parsed for {MusicId}: {Reason}", id, reason);
            return new MetadataResolution.Fallback("metadata-deserialize-failed");
        }

        MetadataResolution.Fallback RateLimited(long id)
        {
            _log.Warning("Metadata fetch rate-limited for {MusicId}; using fallback naming with rate-limited backoff", id);
            return new MetadataResolution.Fallback("metadata-rate-limited");
        }
    }
}
