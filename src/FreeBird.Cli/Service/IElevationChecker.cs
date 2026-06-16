using FreeBird.Core.Abstractions;

namespace FreeBird.Cli.Service;

/// <summary>
/// Mockable abstraction over the "is the current process running with admin rights?" check.
///
/// Two implementations exist in the Cli assembly:
///   - <see cref="NonWindowsElevationChecker"/> (T08): cross-platform fallback that
///     unconditionally returns <c>false</c> so install/uninstall guards trip with a
///     clean error on non-Windows hosts.
///   - <c>WindowsElevationChecker</c> (T10): real check via
///     <c>WindowsIdentity.GetCurrent()</c> + <c>WindowsPrincipal.IsInRole(BuiltinAdministrators)</c>.
///
/// Per design B-5/B-6 both implementations live in <c>FreeBird.Cli</c> — never in
/// <c>FreeBird.Core</c> — because <see cref="FreeBird.Core.DependencyInjection.CoreModule"/>
/// only assembly-scans the Core assembly. Registration is therefore explicit in
/// <c>CliServiceModule</c> (T14), which selects the impl by <c>OperatingSystem.IsWindows()</c>;
/// this interface is never auto-discovered.
/// </summary>
public interface IElevationChecker : IDependency
{
    /// <summary>
    /// True iff the current process is running with administrator/elevated privileges.
    /// </summary>
    bool IsAdmin();
}
