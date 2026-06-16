using FreeBird.Core.Service;

namespace FreeBird.Cli.Service;

/// <summary>
/// Immutable request DTO describing a service registration to hand to
/// <see cref="IServiceController.Install(ServiceInstallRequest)"/>.
///
/// All fields are populated from the merged CLI flags + JSON config (see design §2.3).
/// </summary>
/// <param name="ShortName">
/// SCM short / service name (e.g. <c>"freebird"</c>). Maps to <c>sc.exe</c>'s name argument
/// and to <c>ServiceConfig.Name</c> in the JSON config.
/// </param>
/// <param name="DisplayName">Human-readable name shown in <c>services.msc</c>.</param>
/// <param name="Description">SCM description string.</param>
/// <param name="BinPath">
/// Absolute path to the service host executable (typically <c>fb.exe</c>) plus its launch
/// arguments. The caller is responsible for quoting any path containing spaces.
/// </param>
/// <param name="Account">
/// Service account override. <c>null</c> means <c>LocalSystem</c> (the design §2.3 default).
/// Otherwise a domain account such as <c>"NT AUTHORITY\\NetworkService"</c> or <c>"DOMAIN\\user"</c>.
/// </param>
/// <param name="Password">
/// Password for <see cref="Account"/>. <c>null</c> when <see cref="Account"/> is <c>null</c> or
/// when the host will gather it via an interactive prompt before invoking <c>Install</c>.
/// </param>
/// <param name="RestartPolicy">
/// SCM failure-action policy (Manual / Restart / Backoff). Reuses the Core-side
/// <see cref="RestartPolicy"/> enum so the JSON config and the SCM request share a single
/// source of truth.
/// </param>
public sealed record ServiceInstallRequest(
    string ShortName,
    string DisplayName,
    string Description,
    string BinPath,
    string? Account,
    string? Password,
    RestartPolicy RestartPolicy);
