namespace FreeBird.Core.Models;

/// <summary>
/// Single source of truth for v3 metadata-related default values. Any change to these
/// defaults must be made here so the CLI option declarations, the ScanOptions/WatchOptions
/// records, and MetadataAwareFileNamer stay in sync.
/// </summary>
public static class MetadataDefaults
{
    public const string NamingTemplate = "{artist} - {title}";
    public const int ApiTimeoutSeconds = 10;
    public const int ApiRateLimit = 0;
    public const int ApiTimeoutMin = 1;
    public const int ApiTimeoutMax = 300;
    public const int ApiRateLimitMin = 0;
    public const int ApiRateLimitMax = 100;
}
