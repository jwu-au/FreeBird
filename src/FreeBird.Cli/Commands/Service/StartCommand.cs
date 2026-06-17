using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Cli.Service;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// T20 — OS-agnostic core of <c>fb service start</c>.
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
/// already-running-gate → start. Exit codes (design §3, LOCKED): 0 success, 1 not admin,
/// 2 not installed, 3 already running, 4 start failed.</para>
/// </summary>
public static class StartCommand
{
    /// <summary>
    /// Runs the start flow. Returns the process exit code.
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
            stderr.Write(AdminElevationMessage.Build("start"));
            return 1;
        }

        // 2. Installed gate — nothing to start.
        if (!controller.Exists(serviceName))
        {
            stderr.WriteLine($"Service '{serviceName}' is not installed; nothing to start.");
            return 2;
        }

        // 3. Already-running gate — only the Running state is treated as a no-op so the
        //    caller gets a precise signal; transitional states fall through to the start
        //    attempt (the SCM start is idempotent enough to wait on a pending start).
        if (controller.Query(serviceName).State == ServiceState.Running)
        {
            stderr.WriteLine($"Service '{serviceName}' is already running.");
            return 3;
        }

        // 4. Start. Any SCM failure maps to exit 4.
        try
        {
            await controller.StartAsync(serviceName, timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"start failed: {ex.Message}");
            return 4;
        }

        // 5. Success.
        stdout.WriteLine($"Started the {serviceName} service.");
        return 0;
    }
}
