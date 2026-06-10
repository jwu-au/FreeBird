using System;
using System.IO;
using System.Text;
using System.Text.Json;
using FreeBird.Core.Models;
using Serilog;

namespace FreeBird.Core.Processing;

/// <summary>
/// T03 — concrete writer/reader for <see cref="ResolutionMarker"/> JSON files.
///
/// Responsibilities:
/// 1. Compute the canonical marker path from <c>(outputDir, sourceStem)</c>.
/// 2. Write markers atomically using the <c>.tmp → File.Move(overwrite: true)</c>
///    pattern (mirrors <c>FileProcessor.QuarantineFile</c>'s sidecar writer).
/// 3. Read markers leniently — never throws; returns <c>false</c> on missing
///    or malformed input.
/// 4. Per D2/T2: marker writes are advisory. On <see cref="IOException"/> or
///    <see cref="UnauthorizedAccessException"/>, we log WARN, best-effort clean
///    up any half-written <c>.tmp</c>, and return normally. The audio file is
///    the user-visible artifact; a missing marker just means the next poll
///    will retry the (already-completed) work.
///
/// <para>
/// This class is intentionally NOT marked <c>IDependency</c>; it's registered
/// explicitly in <c>CoreModule</c> as <c>SingleInstance</c> alongside the
/// other stateless processing helpers.
/// </para>
/// </summary>
public sealed class ResolutionMarkerSerializer
{
    /// <summary>Subdirectory under the output dir that holds marker files.</summary>
    public const string MarkerSubdir = ".freebird-resolved";

    private readonly ILogger _logger;

    public ResolutionMarkerSerializer(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds the canonical marker path:
    /// <c>&lt;outputDir&gt;/.freebird-resolved/&lt;sourceStem&gt;.json</c>.
    /// </summary>
    public static string MarkerPath(string outputDir, string sourceStem) =>
        Path.Combine(outputDir, MarkerSubdir, sourceStem + ".json");

    /// <summary>
    /// Atomically write the marker to disk via <c>.tmp → File.Move(overwrite: true)</c>.
    ///
    /// Per D2/T2: catches <see cref="IOException"/> and
    /// <see cref="UnauthorizedAccessException"/>, logs WARN, best-effort deletes
    /// any <c>.tmp</c> orphan, and returns normally. The caller treats marker
    /// writes as advisory; the audio file is already on disk.
    ///
    /// IMPORTANT: <see cref="OperationCanceledException"/> is intentionally NOT
    /// caught — graceful shutdown must bubble to the orchestrator.
    /// </summary>
    public void WriteAtomic(string outputDir, ResolutionMarker marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDir);
        ArgumentNullException.ThrowIfNull(marker);

        var finalPath = MarkerPath(outputDir, marker.SourceStem);
        var tmpPath = finalPath + ".tmp";
        var dir = Path.GetDirectoryName(finalPath)!;

        try
        {
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(marker, ResolutionMarkerJson.Options);
            File.WriteAllText(tmpPath, json, Encoding.UTF8);
            File.Move(tmpPath, finalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // D2/T2: marker is advisory. Log once, attempt best-effort cleanup,
            // and return — audio is still on disk; next poll will retry.
            _logger.Warning(
                ex,
                "Marker write failed for {SourceStem}; audio is still on disk; next poll will retry",
                marker.SourceStem);

            // Best-effort delete of the .tmp orphan. Swallow ANY error — we've
            // already logged the primary failure; no need to log twice.
            try { if (File.Exists(tmpPath)) { File.Delete(tmpPath); } }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Read + parse a marker file. Returns <c>false</c> if the file is missing
    /// (no log) or if the file exists but JSON parsing fails (logs WARN).
    /// Never throws.
    /// </summary>
    public bool TryRead(string path, out ResolutionMarker? marker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            marker = null;
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            marker = JsonSerializer.Deserialize<ResolutionMarker>(json, ResolutionMarkerJson.Options);
            return marker != null;
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "Failed to parse resolution marker at {Path}", path);
            marker = null;
            return false;
        }
    }
}
