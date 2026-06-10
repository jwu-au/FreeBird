using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Models;
using Serilog;

namespace FreeBird.Core.Watch;

/// <summary>
/// The v2 <see cref="ISkipDecider"/>. Decides whether a candidate source <c>.uc</c>/<c>.uc!</c>
/// should be processed this watch cycle, based on:
/// <list type="number">
///   <item><description><see cref="SkipReason.SourceTooSmall"/> — source is below <see cref="WatchOptions.MinFileSizeBytes"/>.</description></item>
///   <item><description><see cref="SkipReason.AlreadyDecoded"/> — a successful output (<c>.mp3</c>/<c>.flac</c>/<c>.m4a</c>) exists in <see cref="WatchOptions.OutputDir"/>.</description></item>
///   <item><description><see cref="SkipReason.SourceUnchangedSinceFailure"/> — a sidecar under <c>&lt;OutputDir&gt;/.freebird-failed/</c> records the same (size, mtime) as the source today.</description></item>
/// </list>
/// First match wins. <see cref="SkipReason.NotYetStable"/> is out of scope; the watch loop calls
/// <see cref="ICompletionDetector"/> separately before invoking this decider.
/// </summary>
public sealed class FilesystemSkipDecider : ISkipDecider
{
    private static readonly string[] OutputExtensions = { ".mp3", ".flac", ".m4a" };
    private const string FailedDirName = ".freebird-failed";

    private readonly ISidecarReader _sidecarReader;
    private readonly ILogger _logger;

    public FilesystemSkipDecider(ISidecarReader sidecarReader, ILogger logger)
    {
        _sidecarReader = sidecarReader ?? throw new ArgumentNullException(nameof(sidecarReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SkipDecision> DecideAsync(
        string sourcePath,
        WatchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        // Defensive: stat the source. If it's missing or unreadable, hand off to Process
        // (caller / FileProcessor will deal with it). NotYetStable is not our concern.
        FileInfo info;
        try
        {
            info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                return SkipDecision.Process();
            }
        }
        catch (IOException)
        {
            return SkipDecision.Process();
        }
        catch (UnauthorizedAccessException)
        {
            return SkipDecision.Process();
        }

        // 2. SourceTooSmall
        if (info.Length < options.MinFileSizeBytes)
        {
            var decision = SkipDecision.Skip(
                SkipReason.SourceTooSmall,
                $"size={info.Length} < min={options.MinFileSizeBytes}");
            _logger.Debug("Skip decision: {Path} -> {Reason}", sourcePath, decision.Reason);
            return decision;
        }

        var stem = StemBasedFileNamer.GetStem(sourcePath);

        // 3. AlreadyDecoded — first existing successful output wins.
        //
        // v2 names: <stem>.{ext} (e.g. "42-song.mp3"). v3 produces one of two shapes:
        //   * Success: "{artist} - {title}.{ext}"  — the skip decider CANNOT predict
        //     this without an API call (that would defeat skip-fast). Spec OA2 accepts
        //     the resulting re-decode after a failed→successful metadata transition.
        //   * Fallback: "{musicId}.{ext}"  — predictable for sources whose stem starts
        //     with the musicId (the canonical NetEase cache naming, including the
        //     composite "<id>-<bitrate>.uc" form). We check this conservatively.
        //
        // Conservative musicId-fallback handling (per spec §10 / OA2):
        //   - When --offline=true, the current run WILL produce {musicId}.{ext}, so an
        //     existing {musicId}.{ext} is a true Skip.
        //   - When --offline=false, an existing {musicId}.{ext} usually means a PRIOR
        //     run failed metadata. The current run may now succeed (different output
        //     filename). We bias toward Process and log the edge case.
        var musicIdStem = ExtractLeadingMusicId(stem);
        foreach (var ext in OutputExtensions)
        {
            // Legacy v2 / stem-equals-output match (still the primary signal for
            // tests / external callers that pre-populate stem-named outputs).
            var candidate = Path.Combine(options.OutputDir, stem + ext);
            if (File.Exists(candidate))
            {
                var decision = SkipDecision.Skip(SkipReason.AlreadyDecoded, $"exists: {candidate}");
                _logger.Debug("Skip decision: {Path} -> {Reason}", sourcePath, decision.Reason);
                return decision;
            }

            // v3 musicId-fallback match (only useful when the stem itself isn't already
            // the musicId — otherwise the loop above already covered it).
            if (musicIdStem is not null && !string.Equals(musicIdStem, stem, StringComparison.Ordinal))
            {
                var musicIdCandidate = Path.Combine(options.OutputDir, musicIdStem + ext);
                if (File.Exists(musicIdCandidate))
                {
                    if (options.Offline)
                    {
                        var decision = SkipDecision.Skip(SkipReason.AlreadyDecoded, $"exists: {musicIdCandidate}");
                        _logger.Debug("Skip decision: {Path} -> {Reason}", sourcePath, decision.Reason);
                        return decision;
                    }

                    // Online: musicId-only output exists but v3 naming would differ once
                    // metadata resolves. Re-process; document the edge case for operators.
                    _logger.Information(
                        "Skip-edge: musicId-only output exists ({Output}) but v3 naming would differ when metadata resolves; re-processing {Source}",
                        musicIdCandidate, sourcePath);
                }
            }
        }

        // 4. SourceUnchangedSinceFailure — scan .freebird-failed/<stem>.*.txt sidecars.
        var failedDir = Path.Combine(options.OutputDir, FailedDirName);
        if (Directory.Exists(failedDir))
        {
            string[] candidateSidecars;
            try
            {
                candidateSidecars = Directory.GetFiles(failedDir, stem + ".*.txt");
            }
            catch (IOException)
            {
                candidateSidecars = Array.Empty<string>();
            }
            catch (UnauthorizedAccessException)
            {
                candidateSidecars = Array.Empty<string>();
            }

            var currentMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

            foreach (var sidecarPath in candidateSidecars)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SidecarRecord? record;
                try
                {
                    record = await _sidecarReader.TryReadAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (record is null)
                {
                    continue;
                }
                if (record.SourceSize is null || record.SourceMtime is null)
                {
                    // v1 sidecar — no size/mtime evidence to compare against. Don't skip on this.
                    continue;
                }

                if (record.SourceSize == info.Length && record.SourceMtime == currentMtime)
                {
                    var decision = SkipDecision.Skip(
                        SkipReason.SourceUnchangedSinceFailure,
                        $"matches sidecar: {sidecarPath}");
                    _logger.Debug("Skip decision: {Path} -> {Reason}", sourcePath, decision.Reason);
                    return decision;
                }
            }
        }

        // 5. Default: Process
        _logger.Debug("Skip decision: {Path} -> Process", sourcePath);
        return SkipDecision.Process();
    }

    /// <summary>
    /// Extract the leading-digit musicId from a .uc/.uc! stem, matching
    /// <see cref="Naming.MetadataAwareFileNamer"/>'s fallback rule so that the skip
    /// decider and the namer agree on what <c>{musicId}.{ext}</c> means. Returns
    /// <c>null</c> when no leading digits are present (degenerate case; skip decider
    /// cannot predict a musicId-fallback name and falls through to Process).
    /// </summary>
    private static string? ExtractLeadingMusicId(string stem)
    {
        if (string.IsNullOrEmpty(stem)) { return null; }
        int i = 0;
        while (i < stem.Length && stem[i] >= '0' && stem[i] <= '9')
        {
            i++;
        }
        return i > 0 ? stem.Substring(0, i) : null;
    }
}
