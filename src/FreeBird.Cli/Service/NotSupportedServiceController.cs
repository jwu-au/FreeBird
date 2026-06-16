using System;
using System.Threading;
using System.Threading.Tasks;

namespace FreeBird.Cli.Service;

/// <summary>
/// Cross-platform fallback <see cref="IServiceController"/> that throws
/// <see cref="PlatformNotSupportedException"/> from every member. Used on
/// non-Windows hosts so <c>fb service</c> commands emit a clean, friendly
/// error instead of attempting Windows-only SCM / P/Invoke calls.
///
/// OS selection happens at DI-binding time in <c>CliServiceModule</c> (T14);
/// this type itself is unconditional by design — do NOT call
/// <c>OperatingSystem.IsWindows()</c> here.
///
/// Async methods throw synchronously (not via <see cref="Task.FromException"/>)
/// because the operation is never supported on this platform — there is no
/// asynchronous work to schedule, and a sync throw is identical from the
/// caller's <c>await</c> perspective while avoiding a needless allocation.
/// </summary>
public sealed class NotSupportedServiceController : IServiceController
{
    private const string NotSupportedMessage =
        "'fb service' is Windows-only. See README §Service for setup on this platform.";

    /// <inheritdoc/>
    public void Install(ServiceInstallRequest req)
        => throw new PlatformNotSupportedException(NotSupportedMessage);

    /// <inheritdoc/>
    public void Uninstall(string shortName)
        => throw new PlatformNotSupportedException(NotSupportedMessage);

    /// <inheritdoc/>
    public Task StartAsync(string shortName, TimeSpan timeout, CancellationToken ct)
        => throw new PlatformNotSupportedException(NotSupportedMessage);

    /// <inheritdoc/>
    public Task StopAsync(string shortName, TimeSpan timeout, CancellationToken ct)
        => throw new PlatformNotSupportedException(NotSupportedMessage);

    /// <inheritdoc/>
    public ServiceStatusInfo Query(string shortName)
        => throw new PlatformNotSupportedException(NotSupportedMessage);

    /// <inheritdoc/>
    public bool Exists(string shortName)
        => throw new PlatformNotSupportedException(NotSupportedMessage);
}
