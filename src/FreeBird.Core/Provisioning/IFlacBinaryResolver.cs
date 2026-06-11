using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Provisioning;

/// <summary>
/// Probes for flac and metaflac binaries in a defined order:
///   1. --flac-bin CLI flag override (if provided)
///   2. <AppContext.BaseDirectory>/flac{.exe} (next to fb executable; covers bundled + auto-downloaded)
///   3. PATH lookup (system-installed)
///
/// Returns the first hit. No version check is performed; the design accepts whatever the user has.
/// On Windows when nothing is found, the resolver may delegate to IFlacAutoInstaller to download from Xiph OSUOSL.
/// </summary>
public interface IFlacBinaryResolver : IDependency
{
    /// <summary>Resolve the path to the flac binary, optionally triggering auto-install on Windows. Returns null if unavailable.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<FlacResolution> ResolveFlacAsync(CancellationToken ct);

    /// <summary>Resolve the path to the metaflac binary, optionally triggering auto-install on Windows. Returns null if unavailable.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<FlacResolution> ResolveMetaflacAsync(CancellationToken ct);
}

/// <summary>Result of a flac/metaflac resolution attempt.</summary>
/// <param name="Path">Full path to the binary, or null if not available.</param>
/// <param name="Provenance">Where the binary was located (logging/diagnostics).</param>
public sealed record FlacResolution(string? Path, FlacBinaryProvenance Provenance)
{
    /// <summary>True if a binary was located.</summary>
    public bool IsAvailable => Path is not null;

    /// <summary>Singleton 'nothing found' result.</summary>
    public static readonly FlacResolution NotFound = new(null, FlacBinaryProvenance.NotFound);
}
