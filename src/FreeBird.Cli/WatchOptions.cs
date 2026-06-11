using System;
using FreeBird.Core.Models;

namespace FreeBird.Cli;

/// <summary>
/// Parsed and validated options for `fb watch`. Populated by <see cref="WatchCommand"/>.
/// T11 will consume this in WatchRunner; for T10 it's parse-and-print-only.
/// </summary>
public sealed class WatchOptions
{
    public required string InputDir { get; init; }
    public required string OutputDir { get; init; }
    public IntegrityLevel Integrity { get; init; } = IntegrityLevel.Auto;
    public int Concurrency { get; init; } = 4;
    public CollisionPolicy Collision { get; init; } = CollisionPolicy.Skip;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int StabilityChecks { get; init; } = 2;
    public long MinFileSize { get; init; } = 1024L;
    public bool SkipInitialScan { get; init; }
    public string? LogFilePath { get; init; }
    public bool NoLogFile { get; init; }
    public bool Verbose { get; init; }
    public bool Quiet { get; init; }

    // T19b — v3 metadata flags, mirrored from Core.Models.WatchOptions / IMetadataOptions.
    public string NamingTemplate { get; init; } = MetadataDefaults.NamingTemplate;
    public bool Offline { get; init; }
    public int ApiTimeoutSeconds { get; init; } = MetadataDefaults.ApiTimeoutSeconds;
    public int ApiRateLimit { get; init; } = MetadataDefaults.ApiRateLimit;
    public bool WriteTags { get; init; }

    // T15 — v3.1 flac provisioning flags. Consumed by WatchRunner via FlacOptionsBinder
    // to override the default FlacResolverOptions in the Autofac container.
    public string? FlacBin { get; init; }
    public string? FlacUrl { get; init; }
    public bool NoAutoDownload { get; init; }

    /// <summary>
    /// Parses a duration string. Accepts: bare integer (= seconds), "Ns" (seconds), "Nm" (minutes).
    /// Rejects all other suffixes (e.g. "5h"). Throws <see cref="ArgumentException"/> on invalid input.
    /// </summary>
    public static TimeSpan ParseDuration(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ArgumentException("empty duration");
        }

        var trimmed = raw.Trim().ToLowerInvariant();

        if (trimmed.EndsWith('s'))
        {
            if (int.TryParse(trimmed[..^1], out var s) && s > 0)
            {
                return TimeSpan.FromSeconds(s);
            }
        }
        else if (trimmed.EndsWith('m'))
        {
            if (int.TryParse(trimmed[..^1], out var m) && m > 0 && m <= 60)
            {
                return TimeSpan.FromMinutes(m);
            }
        }
        else if (int.TryParse(trimmed, out var n) && n > 0)
        {
            return TimeSpan.FromSeconds(n);
        }

        throw new ArgumentException($"invalid duration: {raw} (use Ns or Nm, e.g. 5s, 2m)");
    }
}
