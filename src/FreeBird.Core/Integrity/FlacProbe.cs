using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Provisioning;

namespace FreeBird.Core.Integrity;

/// <summary>
/// Probes for the availability of the `flac` binary via <see cref="IFlacBinaryResolver"/>.
/// Thin wrapper over the resolver that caches the boolean result for the lifetime of this instance.
/// </summary>
/// <remarks>
/// As of v3.1 (T05) this no longer spawns `flac --version` at startup; it relies on the resolver's
/// File.Exists / PATH lookup which is cheap. Auto-install side effects (Windows) still happen lazily
/// inside the resolver when configured.
/// </remarks>
public sealed class FlacProbe : IFlacProbe
{
    private readonly IFlacBinaryResolver _resolver;
    private bool? _cached;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FlacProbe(IFlacBinaryResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_cached.HasValue) { return _cached.Value; }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached.HasValue) { return _cached.Value; }
            var result = await _resolver.ResolveFlacAsync(cancellationToken).ConfigureAwait(false);
            _cached = result.IsAvailable;
            return _cached.Value;
        }
        finally
        {
            _gate.Release();
        }
    }
}
