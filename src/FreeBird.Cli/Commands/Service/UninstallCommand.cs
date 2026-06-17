using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Cli.Service;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// T19 — OS-agnostic core of <c>fb service uninstall</c>.
///
/// <para>The unit-tested surface is the static <see cref="HandleAsync"/> method. All
/// SCM, elevation, and event-log access is funnelled through injected abstractions
/// (<see cref="IElevationChecker"/>, <see cref="IServiceController"/>,
/// <see cref="IEventLogWriter"/>) plus explicit stdin/stdout/stderr streams, so every
/// branch is testable on any OS with mocks — the real Service Control Manager is never
/// touched.</para>
///
/// <para>This is a static helper holding no mutable state, so it is not a public static
/// test seam (AGENTS.md §6). The System.CommandLine wiring is added inline by T22 in
/// <c>CliRoot.Build()</c>; an optional <see cref="Create"/> is not provided here because
/// the handler needs container-resolved dependencies that T22 supplies.</para>
///
/// <para>Order of operations (reviewer-checked): admin-gate → installed-gate →
/// confirmation gate → stop-if-running → uninstall → remove event-log source. Exit codes:
/// 0 success or aborted-by-user, 1 not admin, 2 not installed, 3 SCM error.</para>
/// </summary>
public static class UninstallCommand
{
    /// <summary>Environment variable that, when set to "1", skips the confirmation prompt.</summary>
    private const string ConfirmEnvVar = "FB_SERVICE_UNINSTALL_YES";

    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Inputs for <see cref="HandleAsync"/>. Kept as a small record so the System.CommandLine
    /// wiring (T22) can construct it from parsed options without a long parameter list.
    /// </summary>
    /// <param name="Yes">Skip the interactive confirmation prompt (the <c>--yes</c> flag).</param>
    /// <param name="ServiceName">SCM short name to uninstall. Defaults to <c>FreeBird</c>.</param>
    /// <param name="StopTimeout">
    /// How long to wait for a running service to stop before uninstalling. Defaults to 30s
    /// when null.
    /// </param>
    public sealed record UninstallArgs(bool Yes, string ServiceName = "FreeBird", TimeSpan? StopTimeout = null);

    /// <summary>
    /// Runs the uninstall flow. Returns the process exit code.
    /// </summary>
    public static async Task<int> HandleAsync(
        UninstallArgs args,
        IElevationChecker elevation,
        IServiceController controller,
        IEventLogWriter eventLog,
        TextReader stdin,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (args is null)
        {
            throw new ArgumentNullException(nameof(args));
        }
        if (elevation is null)
        {
            throw new ArgumentNullException(nameof(elevation));
        }
        if (controller is null)
        {
            throw new ArgumentNullException(nameof(controller));
        }
        if (eventLog is null)
        {
            throw new ArgumentNullException(nameof(eventLog));
        }
        if (stdin is null)
        {
            throw new ArgumentNullException(nameof(stdin));
        }
        if (stdout is null)
        {
            throw new ArgumentNullException(nameof(stdout));
        }
        if (stderr is null)
        {
            throw new ArgumentNullException(nameof(stderr));
        }

        // 1. Admin gate — never touch the SCM without elevation.
        if (!elevation.IsAdmin())
        {
            stderr.Write(AdminElevationMessage.Build("uninstall"));
            return 1;
        }

        // 2. Installed gate — nothing to remove.
        if (!controller.Exists(args.ServiceName))
        {
            stderr.WriteLine($"Service '{args.ServiceName}' is not installed; nothing to uninstall.");
            return 2;
        }

        // 3. Confirmation gate — bypassable only via --yes or FB_SERVICE_UNINSTALL_YES=1.
        if (!IsConfirmationSkipped(args.Yes))
        {
            stdout.Write($"This will stop and remove the {args.ServiceName} service. Continue? [y/N]: ");
            var response = await stdin.ReadLineAsync(ct).ConfigureAwait(false);
            if (!IsAffirmative(response))
            {
                stdout.WriteLine("Aborted.");
                return 0;
            }
        }

        // 4-5. Stop-if-running then uninstall. Any SCM failure maps to exit 3.
        try
        {
            var status = controller.Query(args.ServiceName);
            if (ShouldStopBeforeUninstall(status.State))
            {
                await controller
                    .StopAsync(args.ServiceName, args.StopTimeout ?? DefaultStopTimeout, ct)
                    .ConfigureAwait(false);
            }

            controller.Uninstall(args.ServiceName);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"SCM error: {ex.Message}");
            return 3;
        }

        // 6. Remove the event-log source AFTER a successful uninstall.
        eventLog.RemoveSourceIfExists("FreeBird");

        // 7. Success.
        stdout.WriteLine($"Uninstalled the {args.ServiceName} service.");
        return 0;
    }

    /// <summary>
    /// True when the confirmation prompt should be skipped: either the <c>--yes</c> flag was
    /// passed or the <c>FB_SERVICE_UNINSTALL_YES</c> environment variable is exactly "1".
    /// </summary>
    private static bool IsConfirmationSkipped(bool yesFlag)
    {
        if (yesFlag)
        {
            return true;
        }
        return Environment.GetEnvironmentVariable(ConfirmEnvVar) == "1";
    }

    /// <summary>
    /// True only when the trimmed, lower-cased response is "y" or "yes". Any other input —
    /// including null (no line), empty, or the default — is treated as a decline.
    /// </summary>
    private static bool IsAffirmative(string? response)
    {
        if (response is null)
        {
            return false;
        }
        var normalized = response.Trim().ToLowerInvariant();
        return normalized == "y" || normalized == "yes";
    }

    /// <summary>
    /// Decides whether the service must be stopped before uninstall. We stop when the SCM
    /// reports the service Running, and conservatively also when a start is pending (so we
    /// don't try to remove a service that is mid-transition into the Running state).
    /// </summary>
    private static bool ShouldStopBeforeUninstall(ServiceState state) =>
        state == ServiceState.Running || state == ServiceState.StartPending;
}
