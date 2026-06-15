namespace FreeBird.Core.Service;

/// <summary>
/// Restart behaviour applied to the Windows Service Control Manager after a non-zero exit.
/// Mirrors design §2.7.
/// </summary>
public enum RestartPolicy
{
    /// <summary>SCM leaves the service stopped; an operator must restart it.</summary>
    Manual,
    /// <summary>SCM restarts immediately on any failure.</summary>
    Restart,
    /// <summary>SCM restarts with an exponential backoff (1m, 5m, 15m).</summary>
    Backoff,
}

/// <summary>
/// Integrity-check level mirrored from the <c>--integrity</c> CLI flag.
/// </summary>
public enum IntegrityChoice
{
    /// <summary>No integrity checks.</summary>
    Off,
    /// <summary>Level 1: cheap header / size sanity checks only.</summary>
    L1,
    /// <summary>Level 3: full decode + checksum verification.</summary>
    L3,
    /// <summary>Choose level based on file size / available CPU.</summary>
    Auto,
}

/// <summary>
/// Root of the JSON config file consumed by <c>fb service install</c>.
/// Tolerates a leading <c>$schema</c> property for IDE autocomplete.
/// </summary>
public sealed record RootConfig
{
    /// <summary>Optional <c>$schema</c> URL/path; ignored at runtime, present only for IDE tooling.</summary>
    public string? Schema { get; init; }
    public WatchConfig Watch { get; init; } = new();
    public ServiceConfig Service { get; init; } = new();
}

/// <summary>
/// Mirrors the arguments accepted by <c>fb watch</c> when run inside the Windows Service host.
/// </summary>
public sealed record WatchConfig
{
    public string[] Inputs { get; init; } = Array.Empty<string>();
    public string Output { get; init; } = "";
    public IntegrityChoice Integrity { get; init; } = IntegrityChoice.Auto;
    public int Concurrency { get; init; } = 4;
    public int ApiConcurrency { get; init; } = 4;
    public int PollIntervalSeconds { get; init; } = 5;  // design default
    public int StabilityChecks { get; init; } = 2;
    public long MinFileSize { get; init; } = 1024;
    public bool WriteTags { get; init; } = true;
    public string? LogFile { get; init; }

    // --- Round 5 fix M-4: service-host-only knobs that the Cli flags expose elsewhere ---

    /// <summary>Optional FLAC binary discovery overrides; null = use existing CLI resolution path.</summary>
    public FlacConfig? Flac { get; init; }

    /// <summary>Optional NetEase API rate limit (req/sec); null = use <c>MetadataDefaults.ApiRateLimit</c>.</summary>
    public double? ApiRateLimit { get; init; }
}

/// <summary>
/// Windows Service Control Manager registration metadata.
/// </summary>
public sealed record ServiceConfig
{
    public string Name { get; init; } = ConfigDefaults.DefaultServiceName;
    public string DisplayName { get; init; } = ConfigDefaults.DefaultDisplayName;
    public string Description { get; init; } = ConfigDefaults.DefaultDescription;
    public RestartPolicy RestartPolicy { get; init; } = ConfigDefaults.DefaultRestartPolicy;
    public bool LogToEventLog { get; init; } = true;
}

/// <summary>
/// FLAC binary discovery / installer behaviour overrides for service mode (Round 5 fix M-4).
/// </summary>
public sealed record FlacConfig
{
    /// <summary>Absolute path to <c>flac</c>/<c>flac.exe</c>; mirrors <c>--flac-bin</c>.</summary>
    public string? Bin { get; init; }

    /// <summary>Override URL for the FLAC binary auto-installer; mirrors <c>FREEBIRD_FLAC_URL</c>.</summary>
    public string? Url { get; init; }

    /// <summary>When true, skip the Windows auto-install flow.</summary>
    public bool DisableAutoInstall { get; init; } = false;
}
