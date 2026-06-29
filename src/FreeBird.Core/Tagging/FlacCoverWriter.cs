using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Provisioning;
using Serilog;

namespace FreeBird.Core.Tagging;

/// <summary>
/// Embeds cover art into FLAC files via the <c>metaflac</c> subprocess.
///
/// Why subprocess (not TagLibSharp): <c>metaflac --import-picture-from</c> only
/// writes the PICTURE metadata block; it never rewrites audio frames, so the
/// streaminfo PCM-MD5 stays intact and FLAC integrity checks keep passing. This
/// mirrors <see cref="FlacTagWriter"/>'s reasoning for tags.
///
/// The metaflac binary is resolved via <see cref="IFlacBinaryResolver"/>; if no
/// binary is found, a <see cref="FlacNotAvailableException"/> is thrown for the
/// caller to translate into pipeline policy.
///
/// Used only by the NCM pipeline (via <see cref="CompositeCoverWriter"/>); the
/// existing <c>.uc</c> tag path is unaffected.
/// </summary>
public sealed class FlacCoverWriter : IFlacCoverWriter, IDependency
{
    private readonly IProcessRunner _runner;
    private readonly IFlacBinaryResolver _resolver;
    private readonly ILogger _log;

    public FlacCoverWriter(IProcessRunner runner, IFlacBinaryResolver resolver, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(log);
        _runner = runner;
        _resolver = resolver;
        _log = log.ForContext<FlacCoverWriter>();
    }

    /// <summary>
    /// Embed <paramref name="coverBytes"/> into <paramref name="filePath"/>. Stages
    /// the image to a temp file (metaflac reads picture data from a path, not stdin),
    /// removes any existing PICTURE blocks for idempotence, then imports the new one.
    /// The temp file is always deleted in a finally block.
    /// Throws <see cref="FlacNotAvailableException"/> when metaflac cannot be located.
    /// </summary>
    public async Task<TagWriteResult> WriteCoverAsync(string filePath, byte[] coverBytes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(coverBytes);

        var metaflacResolution = await _resolver.ResolveMetaflacAsync(ct).ConfigureAwait(false);
        if (!metaflacResolution.IsAvailable)
        {
            throw new FlacNotAvailableException(
                "metaflac binary not available for FLAC cover write");
        }

        var mime = CoverMime.Detect(coverBytes);
        var tempImagePath = Path.Combine(
            Path.GetTempPath(),
            $"fb-cover-{Guid.NewGuid():N}{ExtensionForMime(mime)}");

        try
        {
            await File.WriteAllBytesAsync(tempImagePath, coverBytes, ct).ConfigureAwait(false);

            // metaflac refuses to mix a "major" operation (--remove) with a
            // "shorthand" operation (--import-picture-from) in one invocation, so
            // this is two sequential calls: remove existing PICTURE blocks for
            // idempotence, then import the new one. --remove exits 0 even when no
            // PICTURE block exists, so the first call is a harmless no-op on fresh
            // files.
            ProcessResult removeResult;
            ProcessResult importResult;
            try
            {
                removeResult = await _runner
                    .RunAsync(metaflacResolution.Path!, BuildRemoveArgs(filePath), ct)
                    .ConfigureAwait(false);
                if (removeResult.ExitCode != 0)
                {
                    _log.Warning(
                        "metaflac exit {Exit} removing old cover from {Path}: {Stderr}",
                        removeResult.ExitCode, filePath, removeResult.StandardError);
                    return new TagWriteResult.Failed("cover-write-failed");
                }

                importResult = await _runner
                    .RunAsync(metaflacResolution.Path!, BuildImportArgs(filePath, mime, tempImagePath), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Resolver verified existence, but the binary may have vanished between
                // resolve and exec (race / AV quarantine on Windows).
                _log.Warning("metaflac failed to launch; cannot write FLAC cover for {Path}", filePath);
                return new TagWriteResult.Failed("cover-tool-missing");
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Unexpected error launching metaflac for cover write {Path}", filePath);
                return new TagWriteResult.Failed("cover-io-error");
            }

            if (importResult.ExitCode != 0)
            {
                _log.Warning(
                    "metaflac exit {Exit} importing cover to {Path}: {Stderr}",
                    importResult.ExitCode, filePath, importResult.StandardError);
                return new TagWriteResult.Failed("cover-write-failed");
            }

            return TagWriteResult.Success.Instance;
        }
        finally
        {
            try { File.Delete(tempImagePath); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// First invocation: remove any existing PICTURE blocks so a re-run yields
    /// exactly one cover. <c>--dont-use-padding</c> keeps the metadata block from
    /// growing across runs. Exits 0 even when no PICTURE block is present.
    /// </summary>
    internal static IReadOnlyList<string> BuildRemoveArgs(string filePath)
    {
        return new List<string>(capacity: 4)
        {
            "--remove",
            "--block-type=PICTURE",
            "--dont-use-padding",
            filePath,
        };
    }

    /// <summary>
    /// Second invocation: import the staged image as the front cover. Uses
    /// metaflac's full specification form
    /// <c>TYPE|MIME|DESCRIPTION|WIDTHxHEIGHTxDEPTH|FILE</c> with TYPE 3 (front
    /// cover) and an explicit MIME.
    /// </summary>
    /// <remarks>
    /// ponytail: dimensions are a fixed <c>1x1x24</c> placeholder rather than the
    /// real image resolution — metaflac would otherwise try to decode the image to
    /// extract them (and refuses on images it can't parse). The PICTURE block's
    /// cosmetic dimensions are ignored by players, which read the embedded image
    /// itself. Upgrade path: parse real dimensions from the JPEG/PNG header if a
    /// player ever needs them.
    /// </remarks>
    internal static IReadOnlyList<string> BuildImportArgs(string filePath, string mime, string tempImagePath)
    {
        return new List<string>(capacity: 2)
        {
            $"--import-picture-from=3|{mime}||1x1x24|{tempImagePath}",
            filePath,
        };
    }

    private static string ExtensionForMime(string mime)
        => mime == "image/png" ? ".png" : ".jpg";
}
