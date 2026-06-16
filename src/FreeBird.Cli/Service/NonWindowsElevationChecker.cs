namespace FreeBird.Cli.Service;

/// <summary>
/// Cross-platform fallback <see cref="IElevationChecker"/> that always reports
/// "not admin". Used on non-Windows hosts so install/uninstall guards trip with
/// a clean, friendly error instead of attempting a Windows-only P/Invoke.
///
/// OS selection happens at DI-binding time in <c>CliServiceModule</c> (T14);
/// this type itself is unconditional by design — do NOT call
/// <c>OperatingSystem.IsWindows()</c> here.
/// </summary>
public sealed class NonWindowsElevationChecker : IElevationChecker
{
    /// <inheritdoc/>
    public bool IsAdmin() => false;
}
