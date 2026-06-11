using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using FreeBird.Core.Provisioning;

namespace FreeBird.Core.Integrity;

/// <summary>
/// L3 integrity checker: runs `flac -t &lt;file&gt;` and inspects the exit code.
/// Passing means the official decoder verified the PCM MD5 stored in STREAMINFO
/// matches the MD5 of decoded audio — the strongest possible check for FLAC.
///
/// The flac binary path is obtained via <see cref="IFlacBinaryResolver"/>; if the
/// resolver cannot locate a binary (and on Windows, auto-install fails), a
/// <see cref="FlacNotAvailableException"/> is thrown so the caller (T13: ScanRunner /
/// WatchRunner) can apply integrity-mode degradation policy (off/l1 → silent,
/// auto → WARN + degrade, l3 → exit 2).
/// </summary>
public sealed class FlacToolIntegrityChecker : IL3IntegrityChecker
{
    private readonly IProcessRunner _processRunner;
    private readonly IFlacBinaryResolver _resolver;

    public FlacToolIntegrityChecker(IProcessRunner processRunner, IFlacBinaryResolver resolver)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public async Task<IntegrityResult> CheckAsync(
        string filePath,
        AudioFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Audio file not found", filePath);
        }

        if (format != AudioFormat.Flac)
        {
            return IntegrityResult.Failed(IntegrityLevel.L3, $"L3 only supports FLAC; got {format}");
        }

        var flacResolution = await _resolver.ResolveFlacAsync(cancellationToken).ConfigureAwait(false);
        if (!flacResolution.IsAvailable)
        {
            throw new FlacNotAvailableException(
                "flac binary not available for L3 integrity check");
        }

        try
        {
            var result = await _processRunner.RunAsync(
                flacResolution.Path!,
                new[] { "-t", "-s", filePath },
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                return IntegrityResult.Passed(IntegrityLevel.L3);
            }

            var reason = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"flac -t exited with {result.ExitCode}"
                : $"flac -t failed: {result.StandardError.Trim()}";
            return IntegrityResult.Failed(IntegrityLevel.L3, reason);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Defensive: resolver verifies File.Exists, but the binary may be deleted
            // between resolve and exec (race or AV quarantine). Surface a Failed result
            // rather than throwing, matching subprocess-failure semantics.
            return IntegrityResult.Failed(IntegrityLevel.L3, "flac binary failed to launch");
        }
    }
}
