using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Models;

/// <summary>
/// Default <see cref="IMetadataOptions"/> implementation registered in
/// <see cref="DependencyInjection.CoreModule"/> as a SingleInstance.
///
/// Why this exists: <c>MetadataAwareFileNamer</c> (v3 <see cref="IFileNamer"/>) takes
/// <see cref="IMetadataOptions"/> as a constructor dependency, but per-run options
/// (<c>ScanOptions</c>, <c>WatchOptions</c>) flow through orchestrator method
/// parameters, NOT the container. The container needs *some* binding for the
/// graph to resolve at startup; this record provides spec defaults
/// (template="{artist} - {title}", Offline=false, ApiTimeoutSeconds=10,
/// ApiRateLimit=0, WriteTags=false). The actual per-invocation options
/// override these via the resolver/processor call path (T14+).
///
/// This type is deliberately NOT registered via <see cref="IDependency"/> —
/// it's an options record, not a service. CoreModule registers it explicitly.
/// </summary>
public sealed record DefaultMetadataOptions : IMetadataOptions
{
    public string NamingTemplate { get; init; } = "{artist} - {title}";
    public bool Offline { get; init; } = false;
    public int ApiTimeoutSeconds { get; init; } = 10;
    public int ApiRateLimit { get; init; } = 0;
    public bool WriteTags { get; init; } = false;
}
