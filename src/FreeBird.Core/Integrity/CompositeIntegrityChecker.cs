using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using FreeBird.Core.Provisioning;

namespace FreeBird.Core.Integrity;

/// <summary>
/// Routes integrity checks to L1 or L3 based on configured level, file format, and flac availability.
/// </summary>
public sealed class CompositeIntegrityChecker : ICompositeIntegrityChecker
{
    private readonly IL1IntegrityChecker _l1;
    private readonly IL3IntegrityChecker _l3;
    private readonly IFlacProbe _flacProbe;

    public CompositeIntegrityChecker(
        IL1IntegrityChecker l1,
        IL3IntegrityChecker l3,
        IFlacProbe flacProbe)
    {
        _l1 = l1 ?? throw new ArgumentNullException(nameof(l1));
        _l3 = l3 ?? throw new ArgumentNullException(nameof(l3));
        _flacProbe = flacProbe ?? throw new ArgumentNullException(nameof(flacProbe));
    }

    /// <summary>
    /// IIntegrityChecker.CheckAsync without explicit level — defaults to Auto.
    /// </summary>
    public Task<IntegrityResult> CheckAsync(
        string filePath,
        AudioFormat format,
        CancellationToken cancellationToken = default)
        => CheckAsync(filePath, format, IntegrityLevel.Auto, cancellationToken);

    public async Task<IntegrityResult> CheckAsync(
        string filePath,
        AudioFormat format,
        IntegrityLevel level,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (level == IntegrityLevel.Off)
        {
            return IntegrityResult.Skipped();
        }

        var effective = await ResolveEffectiveLevelAsync(level, format, cancellationToken).ConfigureAwait(false);

        if (effective == IntegrityLevel.L3)
        {
            try
            {
                return await _l3.CheckAsync(filePath, format, cancellationToken).ConfigureAwait(false);
            }
            catch (FlacNotAvailableException)
            {
                // T13 degradation policy: the resolver returned NotFound between the
                // startup probe and this L3 call (rare race — env changed mid-run, or
                // probe was skipped, e.g., in unit-test composition).
                //   * level == L3: user explicitly asked for L3 — never silently
                //     downgrade. Quarantine the file with a clear reason.
                //   * level == Auto (or anything else that resolved to L3): degrade
                //     to L1. The startup probe should have warned the user already.
                if (level == IntegrityLevel.L3)
                {
                    return IntegrityResult.Failed(IntegrityLevel.L3, "flac binary not available for L3 check");
                }
                return await _l1.CheckAsync(filePath, format, cancellationToken).ConfigureAwait(false);
            }
        }

        if (effective == IntegrityLevel.L1)
        {
            return await _l1.CheckAsync(filePath, format, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Unexpected effective level: {effective}");
    }

    private async Task<IntegrityLevel> ResolveEffectiveLevelAsync(
        IntegrityLevel requested,
        AudioFormat format,
        CancellationToken ct)
    {
        // L3 only applies to FLAC; non-FLAC falls back to L1
        if (requested == IntegrityLevel.L3)
        {
            return format == AudioFormat.Flac ? IntegrityLevel.L3 : IntegrityLevel.L1;
        }

        // Auto: FLAC + flac binary available => L3; otherwise L1
        if (requested == IntegrityLevel.Auto)
        {
            if (format != AudioFormat.Flac) { return IntegrityLevel.L1; }
            var flacAvailable = await _flacProbe.IsAvailableAsync(ct).ConfigureAwait(false);
            return flacAvailable ? IntegrityLevel.L3 : IntegrityLevel.L1;
        }

        // L1: always L1
        return IntegrityLevel.L1;
    }
}
