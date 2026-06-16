using System.Runtime.Versioning;
using System.Security.Principal;

namespace FreeBird.Cli.Service;

/// <summary>
/// Production <see cref="IElevationChecker"/> that asks Windows whether the
/// current process token is a member of the built-in Administrators group.
///
/// Used as the Windows arm of the OS-conditional binding in <c>CliServiceModule</c>
/// (T14): on Windows this overrides <see cref="NonWindowsElevationChecker"/> so
/// the install/uninstall guards reflect real elevation state.
/// </summary>
/// <remarks>
/// The check is performed fresh on every call — the result is deliberately NOT
/// cached, because elevation can change within a process lifetime (e.g. a token
/// obtained via <c>runas</c> / UAC consent). The <see cref="WindowsIdentity"/>
/// returned by <see cref="WindowsIdentity.GetCurrent()"/> owns a native token
/// handle and is disposed via <c>using</c> on each call to avoid leaking it.
///
/// The whole type is marked <c>[SupportedOSPlatform("windows")]</c>: the
/// <see cref="WindowsIdentity"/> / <see cref="WindowsPrincipal"/> APIs are
/// Windows-only, and the attribute satisfies the CA1416 platform-compatibility
/// analyzer for all members so the assembly still builds warning-free on
/// macOS/Linux (where this type is never instantiated).
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsElevationChecker : IElevationChecker
{
    /// <inheritdoc/>
    public bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
