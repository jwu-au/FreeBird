using System;

namespace FreeBird.Cli.Service;

/// <summary>
/// Immutable snapshot of a service's SCM registration + runtime metadata, returned by
/// <see cref="IServiceController.Query(string)"/> and consumed by <c>fb service status</c>.
/// </summary>
/// <param name="ShortName">SCM short / service name as registered (e.g. <c>"freebird"</c>).</param>
/// <param name="DisplayName">Human-readable name shown in <c>services.msc</c>.</param>
/// <param name="State">Cross-platform state projection — see <see cref="ServiceState"/>.</param>
/// <param name="Account">
/// Service account the SCM is configured to run under (e.g. <c>"LocalSystem"</c>,
/// <c>"NT AUTHORITY\\NetworkService"</c>, <c>"DOMAIN\\user"</c>). <c>null</c> when the service
/// is not installed or the account could not be resolved.
/// </param>
/// <param name="StartType">
/// SCM start type as a free-form string: one of <c>"Auto"</c>, <c>"AutoDelayedStart"</c>,
/// <c>"Manual"</c>, <c>"Disabled"</c>. Kept as a string (not an enum) to (a) stay cross-platform
/// and (b) avoid a premature enum that would have to mirror Windows-only types. <c>null</c>
/// when the service is not installed.
/// </param>
/// <param name="StartedAtUtc">
/// UTC timestamp of the most recent service start, derived in T12 via
/// <c>Process.GetProcessById(...).StartTime</c>. <c>null</c> when the service is not running
/// or the host process cannot be inspected.
/// </param>
/// <param name="ConfigPath">
/// Absolute path to the JSON config file the service was registered with, or <c>null</c>
/// when not yet resolved / not applicable.
/// </param>
/// <param name="LogFilePath">
/// Absolute path to the rolling Serilog file sink, or <c>null</c> when file logging is
/// disabled / not yet resolved.
/// </param>
public sealed record ServiceStatusInfo(
    string ShortName,
    string DisplayName,
    ServiceState State,
    string? Account,
    string? StartType,
    DateTimeOffset? StartedAtUtc,
    string? ConfigPath,
    string? LogFilePath);
