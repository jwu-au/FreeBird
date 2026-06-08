using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Integrity;

/// <summary>
/// L3 integrity checker: runs `flac -t <file>` and inspects the exit code.
/// Passing means the official decoder verified the PCM MD5 stored in STREAMINFO
/// matches the MD5 of decoded audio — the strongest possible check for FLAC.
/// </summary>
public sealed class FlacToolIntegrityChecker : IL3IntegrityChecker
{
    private readonly IProcessRunner _processRunner;

    public FlacToolIntegrityChecker(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
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

        try
        {
            var result = await _processRunner.RunAsync(
                "flac",
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
            return IntegrityResult.Failed(IntegrityLevel.L3, "flac binary not found on PATH");
        }
    }
}
