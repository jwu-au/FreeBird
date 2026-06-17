using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Models;
using FreeBird.Core.Service;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FreeBird.Cli.Service;

/// <summary>
/// T13 — the <see cref="BackgroundService"/> that drives the watch loop when FreeBird runs as a
/// hosted service (Windows SCM via the generic host). It owns no scheduling logic of its own:
/// it projects the parsed <see cref="RootConfig"/> into a core <see cref="WatchOptions"/> and
/// delegates the lifecycle to <see cref="WatchSupervisor"/>.
///
/// Testability seam: <see cref="WatchSupervisor"/> is sealed with a non-virtual
/// <c>RunAsync</c>, so it cannot be mocked or subclassed. Rather than introduce a mutable static
/// test seam (forbidden by repo convention), the only branching logic — the config → options
/// projection — is extracted into the pure, public, static <see cref="ProjectOptions"/> helper,
/// which is unit-tested directly. The full <see cref="ExecuteAsync"/> supervisor interaction
/// (RunAsync forwarding, exception propagation, cancellation drain) is covered by the
/// service-mode E2E tasks (T23/T24) and Windows CI.
/// </summary>
public sealed class WatchHostedService : BackgroundService
{
    private readonly WatchSupervisor _supervisor;
    private readonly RootConfig _config;
    private readonly ILogger<WatchHostedService> _log;

    public WatchHostedService(
        WatchSupervisor supervisor,
        RootConfig config,
        ILogger<WatchHostedService> log)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Pure projection of the service config's <see cref="WatchConfig"/> into the core
    /// <see cref="WatchOptions"/> consumed by <see cref="WatchSupervisor.RunAsync"/>.
    ///
    /// Mapping notes:
    ///   - <c>watch.apiConcurrency</c> is intentionally NOT mapped: core <see cref="WatchOptions"/>
    ///     has no such field. API concurrency is enforced via DI (the SingleInstance
    ///     <c>GlobalApiRateLimiter</c>), not via the options record.
    ///   - <c>watch.apiRateLimit</c> is a nullable double on the config side and an int on the
    ///     core side; null falls back to <see cref="MetadataDefaults.ApiRateLimit"/> (0 = unlimited).
    /// </summary>
    public static FreeBird.Core.Models.WatchOptions ProjectOptions(WatchConfig watch)
    {
        ArgumentNullException.ThrowIfNull(watch);

        return new FreeBird.Core.Models.WatchOptions(
            InputDirs: watch.Inputs,
            OutputDir: watch.Output,
            Integrity: MapIntegrity(watch.Integrity),
            Concurrency: watch.Concurrency,
            PollInterval: TimeSpan.FromSeconds(watch.PollIntervalSeconds),
            StabilityChecks: watch.StabilityChecks,
            MinFileSizeBytes: watch.MinFileSize)
        {
            WriteTags = watch.WriteTags,
            ApiRateLimit = watch.ApiRateLimit.HasValue
                ? (int)Math.Round(watch.ApiRateLimit.Value)
                : MetadataDefaults.ApiRateLimit,
        };
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var (valid, _) = _supervisor.ParseAndValidateInputs(_config.Watch.Inputs);
        if (valid.Count == 0)
        {
            _log.LogWarning(
                "Watch service: no valid input directories configured; nothing to do.");
            return;
        }

        var tasks = _supervisor.CreateTasks(valid);
        var options = ProjectOptions(_config.Watch);

        // Exceptions are intentionally NOT swallowed: a failure here must surface to the host so
        // the service reports an unhealthy / failed state rather than silently idling.
        await _supervisor.RunAsync(tasks, options, ct).ConfigureAwait(false);
    }

    private static IntegrityLevel MapIntegrity(IntegrityChoice choice) => choice switch
    {
        IntegrityChoice.Off => IntegrityLevel.Off,
        IntegrityChoice.L1 => IntegrityLevel.L1,
        IntegrityChoice.L3 => IntegrityLevel.L3,
        IntegrityChoice.Auto => IntegrityLevel.Auto,
        _ => throw new ArgumentOutOfRangeException(
            nameof(choice), choice, "Unknown IntegrityChoice value."),
    };
}
