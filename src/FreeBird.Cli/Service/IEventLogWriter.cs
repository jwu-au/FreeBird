using FreeBird.Core.Abstractions;

namespace FreeBird.Cli.Service;

/// <summary>
/// Mockable abstraction over the install-time Windows Event Log <em>source</em>
/// lifecycle (create on install, remove on uninstall).
///
/// This writer is deliberately install/uninstall-only and holds NO state and NO
/// native handle (M-5). At runtime the service logs through
/// <c>Serilog.Sinks.EventLog</c>, which owns its own <c>EventLog</c> handle — so
/// there is intentionally NO <c>Write(...)</c> method here. The only consumers are
/// the install handler (<c>CreateSourceIfMissing</c>, T18) and the uninstall
/// handler (<c>RemoveSourceIfExists</c>, T19).
///
/// Two implementations exist in the Cli assembly:
///   - <c>WindowsEventLogWriter</c> (T11): real source create/remove via
///     <c>System.Diagnostics.EventLog</c>; carries <c>[SupportedOSPlatform("windows")]</c>
///     and is excluded from the non-Windows compile set (its backing NuGet is
///     Windows-only).
///   - <see cref="NotSupportedEventLogWriter"/> (T11): cross-platform fallback that
///     throws <see cref="System.PlatformNotSupportedException"/> with the §10
///     friendly message so non-Windows hosts get a clean error.
///
/// Per design B-5/B-6 both implementations live in <c>FreeBird.Cli</c> — never in
/// <c>FreeBird.Core</c> — because <see cref="FreeBird.Core.DependencyInjection.CoreModule"/>
/// only assembly-scans the Core assembly. Registration is therefore explicit in
/// <c>CliServiceModule</c> (T14), which selects the impl by
/// <c>OperatingSystem.IsWindows()</c> and uses the default
/// <c>InstancePerLifetimeScope</c> lifetime (stateless utility — NOT
/// <c>SingleInstance</c>); this interface is never auto-discovered.
/// </summary>
public interface IEventLogWriter : IDependency
{
    /// <summary>
    /// Registers the Event Log <paramref name="source"/> under
    /// <paramref name="logName"/> if it does not already exist. Idempotent: calling
    /// it when the source already exists is a no-op and does not throw.
    /// </summary>
    /// <param name="source">The event source name (e.g. <c>FreeBird</c>).</param>
    /// <param name="logName">The target log (e.g. <c>Application</c>).</param>
    void CreateSourceIfMissing(string source, string logName);

    /// <summary>
    /// Removes the Event Log <paramref name="source"/> if it exists. Idempotent:
    /// calling it when the source is absent is a no-op and does not throw.
    /// </summary>
    /// <param name="source">The event source name to remove.</param>
    void RemoveSourceIfExists(string source);
}
