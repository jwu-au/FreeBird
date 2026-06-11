using System.IO.Abstractions;
using Serilog;

namespace FreeBird.Core.Provisioning;

/// <summary>
/// Windows-only IFlacAutoInstaller. Downloads the pinned flac ZIP from Xiph OSUOSL,
/// verifies SHA256 (T09), extracts the four required Win64/ binaries (T10), and reports success.
/// </summary>
/// <remarks>
/// T08 added download to temp file; T09 added SHA256 verification and ZIP path-traversal safety;
/// T10 added actual extraction of the four required Win64 binaries plus a post-write existence
/// check (R3 mitigation: antivirus may silently quarantine extracted binaries).
/// </remarks>
public sealed class WindowsFlacAutoInstaller : IFlacAutoInstaller
{
    /// <summary>Name of the temp file written next to the target directory during download.</summary>
    internal const string TempZipName = ".freebird-flac-install.zip.tmp";

    /// <summary>
    /// ZIP entry paths inside flac-1.5.0-win.zip that we extract to the target directory.
    /// The official Xiph ZIP wraps everything in a top-level <c>flac-1.5.0-win/</c> directory,
    /// so the lookup keys must include that prefix to match.
    /// Each entry's basename (Name property — leaf only) becomes a file in targetDirectory.
    /// 32-bit binaries from <c>flac-1.5.0-win/Win32/</c> are intentionally NOT installed.
    /// </summary>
    internal static readonly IReadOnlyList<string> Win64Files = new[]
    {
        "flac-1.5.0-win/Win64/flac.exe",
        "flac-1.5.0-win/Win64/metaflac.exe",
        "flac-1.5.0-win/Win64/libFLAC.dll",
        "flac-1.5.0-win/Win64/libFLAC++.dll",
    };

    /// <summary>
    /// Expected SHA256 of the pinned flac-1.5.0-win.zip from Xiph OSUOSL.
    /// Computed against the canonical archive on 2026-06-11; do NOT change without
    /// updating the pinned URL in tandem.
    /// </summary>
    /// <remarks>
    /// Exposed as a mutable internal property (not const) so unit tests can substitute
    /// the expected hash when feeding hand-built ZIP fixtures. Production code reads it
    /// like a constant; only test code (via <c>InternalsVisibleTo</c>) may mutate it,
    /// and well-behaved tests reset it via IDisposable.
    /// </remarks>
    internal static string ExpectedSha256 { get; set; } = "53f1500f0d6e7c61379d7fee50d4a9f7f504c650009506d9ba015530d76c0dde";

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

        // T09 — SHA256 verification
        string actualSha;
        try
        {
            actualSha = await ComputeSha256Async(tempZipPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempZipPath);
            return new FlacInstallResult.Failed($"hash computation failed: {ex.Message}");
        }

        if (!string.Equals(actualSha, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(tempZipPath);
            return new FlacInstallResult.Failed(
                $"SHA256 mismatch: expected {ExpectedSha256}, got {actualSha} (download may be tampered or corrupt)");
        }

        _log.Information("SHA256 verified ({Hash})", ExpectedSha256);

        // T09 — ZIP path-traversal safety check (no actual extraction yet; T10 will extract)
        try
        {
            ValidateZipEntries(tempZipPath, targetDirectory);
        }
        catch (UnsafeZipEntryException ex)
        {
            TryDeleteFile(tempZipPath);
            return new FlacInstallResult.Failed($"unsafe zip entry rejected: {ex.Message}");
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempZipPath);
            return new FlacInstallResult.Failed($"zip validation failed: {ex.Message}");
        }

        // T10 — Extract the four required Win64 binaries.
        // We pass the accumulator INTO the helper so on-throw partial state is visible
        // to the catch handlers below (otherwise the helper's local list is lost on exception).
        var extractedPaths = new List<string>(Win64Files.Count);
        try
        {
            await ExtractWin64FilesAsync(tempZipPath, targetDirectory, extractedPaths, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDeleteFile(tempZipPath);
            TryDeleteFiles(extractedPaths);
            throw;
        }
        catch (FlacEntryMissingException ex)
        {
            TryDeleteFile(tempZipPath);
            TryDeleteFiles(extractedPaths);
            return new FlacInstallResult.Failed($"required entry missing in archive: {ex.Message}");
        }
        catch (Exception ex)
        {
            TryDeleteFile(tempZipPath);
            TryDeleteFiles(extractedPaths);
            return new FlacInstallResult.Failed($"extraction failed: {ex.Message}");
        }

        // T10 — Post-write existence check (R3: AV may have silently quarantined a file)
        var missingAfterWrite = extractedPaths.Where(p => !_fs.File.Exists(p)).ToList();
        if (missingAfterWrite.Count > 0)
        {
            TryDeleteFiles(extractedPaths);
            TryDeleteFile(tempZipPath);
            var missingNames = string.Join(", ", missingAfterWrite.Select(p => _fs.Path.GetFileName(p)));
            return new FlacInstallResult.Failed(
                $"file(s) missing after extraction (likely quarantined by antivirus): {missingNames}");
        }

        TryDeleteFile(tempZipPath);
        _log.Information("flac binaries installed to {TargetDirectory}", targetDirectory);
        var flacPath = _fs.Path.Combine(targetDirectory, "flac.exe");
        var metaflacPath = _fs.Path.Combine(targetDirectory, "metaflac.exe");
        return new FlacInstallResult.Installed(flacPath, metaflacPath);
    }

    private async Task ExtractWin64FilesAsync(string zipPath, string targetDirectory, List<string> written, CancellationToken ct)
    {
        using var stream = _fs.File.OpenRead(zipPath);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

        foreach (var entryPath in Win64Files)
        {
            ct.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(entryPath);
            if (entry is null)
            {
                throw new FlacEntryMissingException(entryPath);
            }

            // Strip 'Win64/' prefix — file lives directly in targetDirectory
            var basename = entry.Name;  // e.g. 'flac.exe' — Name is the leaf, already minus directory prefix
            var destPath = _fs.Path.Combine(targetDirectory, basename);

            _log.Information("Extracting {Entry} \u2192 {TargetPath}", entryPath, destPath);

            await using (var entryStream = entry.Open())
            await using (var destStream = _fs.File.Create(destPath))
            {
                await entryStream.CopyToAsync(destStream, ct).ConfigureAwait(false);
            }
            written.Add(destPath);
        }
    }

    private void TryDeleteFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            TryDeleteFile(p);
        }
    }

    private async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using var fs = _fs.File.OpenRead(filePath);
        var hashBytes = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Validates every entry in the ZIP file would extract under <paramref name="extractionRoot"/>.
    /// Throws <see cref="UnsafeZipEntryException"/> on the first traversal attempt
    /// (absolute path, <c>..</c> segment, or any other entry that canonicalizes outside the root).
    /// Uses System.IO.Compression directly because System.IO.Abstractions does not abstract
    /// <see cref="System.IO.Compression.ZipArchive"/>; the underlying stream still comes from
    /// <see cref="IFileSystem"/> so MockFileSystem-backed tests work.
    /// </summary>
    private void ValidateZipEntries(string zipPath, string extractionRoot)
    {
        var rootFull = System.IO.Path.GetFullPath(extractionRoot);
        // Normalize trailing separator so the prefix check below is exact (prevents "/app2" matching "/app")
        if (!rootFull.EndsWith(System.IO.Path.DirectorySeparatorChar))
        {
            rootFull += System.IO.Path.DirectorySeparatorChar;
        }

        using var stream = _fs.File.OpenRead(zipPath);
        using var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            // Skip pure directory entries (Name empty, FullName ends with '/')
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                continue;
            }

            var combined = System.IO.Path.Combine(extractionRoot, entry.FullName);
            string canonical;
            try
            {
                canonical = System.IO.Path.GetFullPath(combined);
            }
            catch (Exception ex)
            {
                throw new UnsafeZipEntryException($"cannot canonicalize entry '{entry.FullName}': {ex.Message}");
            }

            if (!canonical.StartsWith(rootFull, StringComparison.Ordinal))
            {
                throw new UnsafeZipEntryException(
                    $"entry '{entry.FullName}' resolves to '{canonical}' which is outside '{rootFull}'");
            }
        }
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

/// <summary>
/// Sentinel exception thrown by <see cref="WindowsFlacAutoInstaller"/> when a ZIP entry
/// would extract outside the installation target (path traversal attempt).
/// Internal — callers see a <see cref="FlacInstallResult.Failed"/> with the message.
/// </summary>
internal sealed class UnsafeZipEntryException : Exception
{
    public UnsafeZipEntryException(string message) : base(message) { }
}

/// <summary>
/// Sentinel exception thrown by <see cref="WindowsFlacAutoInstaller"/> when a required
/// Win64 entry (see <see cref="WindowsFlacAutoInstaller.Win64Files"/>) is absent from the archive.
/// Internal — callers see a <see cref="FlacInstallResult.Failed"/> with the message.
/// </summary>
internal sealed class FlacEntryMissingException : Exception
{
    public FlacEntryMissingException(string entryName) : base($"ZIP entry not found: {entryName}") { }
}
