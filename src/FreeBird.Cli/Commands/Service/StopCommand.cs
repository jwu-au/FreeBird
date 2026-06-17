using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Cli.Service;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// T20 — OS-agnostic core of <c>fb service stop</c>.
///
/// <para>The unit-tested surface is the static <see cref="HandleAsync"/> method. All SCM
/// and elevation access is funnelled through injected abstractions
/// (<see cref="IElevationChecker"/>, <see cref="IServiceController"/>) plus explicit
/// stdout/stderr streams, so every branch is testable on any OS with mocks — the real
/// Service Control Manager is never touched. This static helper holds no mutable state, so
/// it is not a public static test seam (AGENTS.md §6). The System.CommandLine wiring is
/// added by T22.</para>
///
/// <para>Order of operations (reviewer-checked): admin-gate → installed-gate →
/// already-stopped-gate → stop. Exit codes (design §3, LOCKED): 0 success, 1 not admin,
/// 2 not installed, 3 already stopped, 4 stop failed.</para>
/// </summary>
public static class StopCommand
{
    /// <summary>
    /// Runs the stop flow. Returns the process exit code.
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
            stderr.Write(AdminElevationMessage.Build("stop"));
            return 1;
        }

        // 2. Installed gate — nothing to stop.
        if (!controller.Exists(serviceName))
        {
            stderr.WriteLine($"Service '{serviceName}' is not installed; nothing to stop.");
            return 2;
        }

        // 3. Already-stopped gate — only the Stopped state is treated as a no-op.
        if (controller.Query(serviceName).State == ServiceState.Stopped)
        {
            stderr.WriteLine($"Service '{serviceName}' is already stopped.");
            return 3;
        }

        // 4. Stop. Any SCM failure maps to exit 4.
        try
        {
            await controller.StopAsync(serviceName, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"stop failed: {ex.Message}");
            return 4;
        }

        // 5. Success.
        stdout.WriteLine($"Stopped the {serviceName} service.");
        return 0;
    }
}
