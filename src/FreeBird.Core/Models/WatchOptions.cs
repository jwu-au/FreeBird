using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Models;

/// <summary>
/// Configuration for a <c>fb watch</c> run, passed into <c>IWatchOrchestrator.RunAsync</c>.
/// Holds the watch-loop knobs only; CLI-surface concerns (log file path, verbose/quiet)
/// live on the CLI side.
/// </summary>
public sealed record WatchOptions : IMetadataOptions
{
    public WatchOptions(
        string InputDir,
        string OutputDir,
        IntegrityLevel Integrity = IntegrityLevel.Auto,
        int Concurrency = 4,
        CollisionPolicy Collision = CollisionPolicy.Skip,
        TimeSpan? PollInterval = null,
        int StabilityChecks = 2,
        long MinFileSizeBytes = 1024,
        bool SkipInitialScan = false)
    {
        ArgumentNullException.ThrowIfNull(InputDir);
        ArgumentNullException.ThrowIfNull(OutputDir);

        var poll = PollInterval ?? TimeSpan.FromSeconds(5);
        if (poll < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(PollInterval), poll, "PollInterval must be non-negative.");
        }
        ArgumentOutOfRangeException.ThrowIfNegative(StabilityChecks);
        ArgumentOutOfRangeException.ThrowIfNegative(MinFileSizeBytes);

        this.InputDir = InputDir;
        this.OutputDir = OutputDir;
        this.Integrity = Integrity;
        this.Concurrency = Concurrency;
        this.Collision = Collision;
        this.PollInterval = poll;
        this.StabilityChecks = StabilityChecks;
        this.MinFileSizeBytes = MinFileSizeBytes;
        this.SkipInitialScan = SkipInitialScan;
    }

    public string InputDir { get; init; }
    public string OutputDir { get; init; }
    public IntegrityLevel Integrity { get; init; }
    public int Concurrency { get; init; }
    public CollisionPolicy Collision { get; init; }
    public TimeSpan PollInterval { get; init; }
    public int StabilityChecks { get; init; }
    public long MinFileSizeBytes { get; init; }
    public bool SkipInitialScan { get; init; }

    /// <summary>
    /// Filename template using <c>{artist}</c>, <c>{title}</c>, <c>{album}</c>, and
    /// <c>{musicId}</c> placeholders. Default: <c>"{artist} - {title}"</c>.
    /// </summary>
    public string NamingTemplate { get; init; } = "{artist} - {title}";

    /// <summary>
    /// Skip NetEase API entirely; always use musicId fallback naming. Maps to the
    /// <c>--offline</c> CLI flag. Default: <c>false</c>.
    /// </summary>
    public bool Offline { get; init; } = false;

    /// <summary>
    /// API request timeout in seconds. Maps to <c>--api-timeout</c>. Default: 10.
    /// </summary>
    public int ApiTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Cap on API calls per second. <c>0</c> = unlimited. Maps to
    /// <c>--api-rate-limit</c>. Default: 0.
    /// </summary>
    public int ApiRateLimit { get; init; } = 0;

    /// <summary>
    /// Write metadata tags into the decoded file (ID3/Vorbis/MP4). Maps to
    /// <c>--write-tags</c>. Default: <c>false</c>.
    /// </summary>
    public bool WriteTags { get; init; } = false;
}
