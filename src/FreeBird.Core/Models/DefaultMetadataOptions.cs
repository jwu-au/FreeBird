using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Models;

/// <summary>
/// Startup-default <see cref="IMetadataOptions"/> registered in
/// <see cref="DependencyInjection.CoreModule"/> as a SingleInstance.
///
/// Why this exists: <c>IMetadataResolver</c> (and any other container-built
/// consumer of <see cref="IMetadataOptions"/>) needs *some* binding so the
/// Autofac graph can resolve at compose time, before any per-run
/// <c>ScanOptions</c> / <c>WatchOptions</c> exists. Per-invocation options
/// flow through orchestrator and processor method parameters (T14+ / T19a+),
/// so these defaults are only ever observed when no per-run override is in
/// effect — the spec defaults match those on <see cref="ScanOptions"/> and
/// <see cref="WatchOptions"/> (template="{artist} - {title}", Offline=false,
/// ApiTimeoutSeconds=10, ApiRateLimit=0, WriteTags=true).
///
/// This type is deliberately NOT registered via <see cref="IDependency"/> —
/// it's an options record, not a service. CoreModule registers it explicitly.
/// </summary>
public sealed record DefaultMetadataOptions : IMetadataOptions
{
    public string NamingTemplate { get; init; } = MetadataDefaults.NamingTemplate;
    public bool Offline { get; init; } = false;
    public int ApiTimeoutSeconds { get; init; } = MetadataDefaults.ApiTimeoutSeconds;
    public int ApiRateLimit { get; init; } = MetadataDefaults.ApiRateLimit;
    public bool WriteTags { get; init; } = true;
}
