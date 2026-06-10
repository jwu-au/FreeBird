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
        foreach (var ext in OutputExtensions)
        {
            var candidate = Path.Combine(options.OutputDir, stem + ext);
            if (File.Exists(candidate))
            {
                var decision = SkipDecision.Skip(SkipReason.AlreadyDecoded, $"exists: {candidate}");
                _logger.Debug("Skip decision: {Path} -> {Reason}", sourcePath, decision.Reason);
                return decision;
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
}
