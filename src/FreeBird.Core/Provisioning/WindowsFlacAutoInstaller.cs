using System.IO.Abstractions;
using Serilog;

namespace FreeBird.Core.Provisioning;

/// <summary>
/// Windows-only IFlacAutoInstaller. Downloads the pinned flac ZIP from Xiph OSUOSL,
/// verifies SHA256 (T09), extracts the four required Win64/ binaries (T10), and reports success.
/// </summary>
/// <remarks>
/// T08 scope: download only. SHA verify and extraction land in T09 and T10 respectively.
/// While incomplete, this impl returns Failed after a successful download (with a clear reason)
/// so it can be wired through the resolver without producing fake-positive Installed results.
/// </remarks>
public sealed class WindowsFlacAutoInstaller : IFlacAutoInstaller
{
    /// <summary>Name of the temp file written next to the target directory during download.</summary>
    internal const string TempZipName = ".freebird-flac-install.zip.tmp";

    private readonly IHttpDownloader _downloader;
    private readonly IFileSystem _fs;
    private readonly ILogger _log;

    public WindowsFlacAutoInstaller(IHttpDownloader downloader, IFileSystem fs, ILogger log)
    {
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task<FlacInstallResult> InstallAsync(string targetDirectory, string sourceUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return new FlacInstallResult.Failed("target directory is null/empty");
        }
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return new FlacInstallResult.Failed("source URL is null/empty");
        }
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            return new FlacInstallResult.Failed($"invalid URL: {sourceUrl}");
        }

        // Ensure target dir exists (it should — it's AppContext.BaseDirectory — but be defensive)
        try
        {
            _fs.Directory.CreateDirectory(targetDirectory);
        }
        catch (Exception ex)
        {
            return new FlacInstallResult.Failed($"cannot create target directory {targetDirectory}: {ex.Message}");
        }

        var tempZipPath = _fs.Path.Combine(targetDirectory, TempZipName);

        // Best-effort cleanup of stale temp from prior aborted run
        TryDeleteFile(tempZipPath);

        _log.Information("Downloading flac binaries from {Url} to {Path}", uri, tempZipPath);

        try
        {
            // Stream download to temp file
            await using (var dest = _fs.File.Create(tempZipPath))
            {
                var bytes = await _downloader.DownloadAsync(uri, dest, progress: null, ct).ConfigureAwait(false);
                _log.Information("Downloaded {Bytes:N0} bytes to {Path}", bytes, tempZipPath);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDeleteFile(tempZipPath);
            throw;
        }
        catch (HttpDownloadException ex)
        {
            TryDeleteFile(tempZipPath);
            _log.Warning(ex, "Download failed from {Url}", uri);
            return new FlacInstallResult.Failed($"download failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempZipPath);
            _log.Warning(ex, "Unexpected error writing temp zip");
            return new FlacInstallResult.Failed($"unexpected error: {ex.Message}");
        }

        // T09 will add SHA verify here.
        // T10 will add ZIP extraction + post-write check here.
        // For T08, we have the temp file on disk but no extraction. Report incomplete and clean up.
        TryDeleteFile(tempZipPath);
        return new FlacInstallResult.Failed("extraction not yet implemented (T09/T10 will complete)");
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            if (_fs.File.Exists(path))
            {
                _fs.File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Best-effort cleanup of {Path} failed; ignoring", path);
        }
    }
}
