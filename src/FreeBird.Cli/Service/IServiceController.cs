using System;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;

namespace FreeBird.Cli.Service;

/// <summary>
/// Cross-platform abstraction over the Windows Service Control Manager (SCM).
///
/// Two implementations exist in the Cli assembly (T11 + T13):
///   - <c>WindowsServiceController</c>: real SCM operations via
///     <c>System.ServiceProcess.ServiceController</c> + <c>sc.exe</c>.
///   - <c>NotSupportedServiceController</c>: throws on every method for non-Windows
///     hosts so the CLI emits a friendly error instead of a P/Invoke crash.
///
/// Per design B-5/B-6 both implementations live in <c>FreeBird.Cli</c> — never in
/// <c>FreeBird.Core</c> — because <see cref="FreeBird.Core.DependencyInjection.CoreModule"/>
/// only assembly-scans the Core assembly. Registration is therefore explicit in
/// <c>CliServiceModule</c> (T14); this interface is never auto-discovered.
/// </summary>
public interface IServiceController : IDependency
{
    /// <summary>Register the service with the SCM using the supplied request.</summary>
    void Install(ServiceInstallRequest req);

    /// <summary>Unregister the service identified by its SCM short name.</summary>
    void Uninstall(string shortName);

    /// <summary>
    /// Ask the SCM to start the service and wait until it reports <see cref="ServiceState.Running"/>
    /// or <paramref name="timeout"/> elapses.
    /// </summary>
    Task StartAsync(string shortName, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Ask the SCM to stop the service and wait until it reports <see cref="ServiceState.Stopped"/>
    /// or <paramref name="timeout"/> elapses.
    /// </summary>
    Task StopAsync(string shortName, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// Snapshot the SCM registration + runtime metadata for the named service.
    /// Returns a status with <see cref="ServiceState.NotInstalled"/> when no such service exists.
    /// </summary>
    ServiceStatusInfo Query(string shortName);

    /// <summary>
    /// True iff the SCM has a registration for <paramref name="shortName"/>.
    /// Cheap probe used by install/uninstall pre-checks.
    /// </summary>
    bool Exists(string shortName);
}
