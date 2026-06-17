using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Cli.Service;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// T20 — OS-agnostic core of <c>fb service restart</c>.
///
/// <para>The unit-tested surface is the static <see cref="HandleAsync"/> method. All SCM
/// and elevation access is funnelled through injected abstractions
/// (<see cref="IElevationChecker"/>, <see cref="IServiceController"/>) plus explicit
/// stdout/stderr streams, so every branch is testable on any OS with mocks — the real
/// Service Control Manager is never touched. This static helper holds no mutable state, so
/// it is not a public static test seam (AGENTS.md §6). The System.CommandLine wiring is
/// added by T22.</para>
///
/// <para>Order of operations (reviewer-checked): admin-gate → installed-gate → stop phase
/// (skipped when the service is already Stopped) → start phase. A failed stop phase aborts
/// before any start is attempted.</para>
///
/// <para>Exit codes (design §3, LOCKED): 0 success, 1 not admin, 2 not installed, 3 restart
/// failed. Per the M-1 fix, restart has no start-failed code — both a stop-phase and a
/// start-phase SCM failure collapse to the restart-failed code (3) with a phase-qualified
/// stderr message.</para>
/// </summary>
public static class RestartCommand
{
    /// <summary>
    /// Runs the restart flow. Returns the process exit code.
    /// </summary>
    public static async Task<int> HandleAsync(
        string serviceName,
        TimeSpan timeout,
        IElevationChecker elevation,
        IServiceController controller,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (serviceName is null)
        {
            throw new ArgumentNullException(nameof(serviceName));
        }
        if (elevation is null)
        {
            throw new ArgumentNullException(nameof(elevation));
        }
        if (controller is null)
        {
            throw new ArgumentNullException(nameof(controller));
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
            stderr.Write(AdminElevationMessage.Build("restart"));
            return 1;
        }

        // 2. Installed gate — nothing to restart.
        if (!controller.Exists(serviceName))
        {
            stderr.WriteLine($"Service '{serviceName}' is not installed; nothing to restart.");
            return 2;
        }

        // 3. Stop phase — skipped when the service is already Stopped so we don't ask the
        //    SCM to stop a stopped service. A stop-phase failure aborts the restart before
        //    any start is attempted, and maps to exit 3 (never 4).
        if (controller.Query(serviceName).State != ServiceState.Stopped)
        {
            try
            {
                await controller.StopAsync(serviceName, timeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                stderr.WriteLine($"restart failed: stop phase: {ex.Message}");
                return 3;
            }
        }

        // 4. Start phase — a start-phase failure also maps to exit 3 (never 4).
        try
        {
            await controller.StartAsync(serviceName, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"restart failed: start phase: {ex.Message}");
            return 3;
        }

        // 5. Success.
        stdout.WriteLine($"Restarted the {serviceName} service.");
        return 0;
    }
}
