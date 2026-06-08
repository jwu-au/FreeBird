using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Integrity;

/// <summary>
/// Probes for the presence of the `flac` CLI on PATH by running `flac --version`.
/// Caches the result for the lifetime of this instance.
/// </summary>
public sealed class FlacProbe : IFlacProbe
{
    private readonly IProcessRunner _processRunner;
    private bool? _cached;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FlacProbe(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_cached.HasValue) { return _cached.Value; }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached.HasValue) { return _cached.Value; }
            try
            {
                var result = await _processRunner.RunAsync("flac", new[] { "--version" }, cancellationToken)
                    .ConfigureAwait(false);
                _cached = result.ExitCode == 0;
            }
            catch
            {
                // launch failure (e.g. Win32Exception) means binary is unavailable
                _cached = false;
            }
            return _cached.Value;
        }
        finally
        {
            _gate.Release();
        }
    }
}
