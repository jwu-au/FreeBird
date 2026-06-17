using System;
using System.IO;
using FreeBird.Cli.Service;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// T21 — OS-agnostic core of <c>fb service status</c>.
///
/// <para>The unit-tested surface is the static <see cref="Handle"/> method. All SCM access
/// is funnelled through the injected <see cref="IServiceController"/> plus explicit
/// stdout/stderr streams, so every branch is testable on any OS with mocks — the real
/// Service Control Manager is never touched.</para>
///
/// <para>Unlike install/uninstall, querying status does NOT require elevation, so there is
/// deliberately no <see cref="IElevationChecker"/> dependency.</para>
///
/// <para>This is a static helper holding no mutable state, so it is not a public static
/// test seam (AGENTS.md §6). The System.CommandLine wiring is added by T22 in
/// <c>CliRoot.Build()</c>.</para>
///
/// <para>Order of operations: installed-gate → query → render. Rendering and exit-code
/// mapping are delegated to <see cref="StatusFormatter"/> (T16). Exit codes:
/// Running→0, NotInstalled→1, Stopped→2, everything else (Paused / StartPending /
/// StopPending / Other)→3. The not-installed human message is written to stdout because
/// the status block it replaces is itself stdout output.</para>
/// </summary>
public static class StatusCommand
{
    /// <summary>
    /// Renders the status of the named service and returns the process exit code.
    /// </summary>
    public static int Handle(
        string serviceName,
        IServiceController controller,
        TimeProvider clock,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (serviceName is null)
        {
            throw new ArgumentNullException(nameof(serviceName));
        }
        if (controller is null)
        {
            throw new ArgumentNullException(nameof(controller));
        }
        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }
        if (stdout is null)
        {
            throw new ArgumentNullException(nameof(stdout));
        }
        if (stderr is null)
        {
            throw new ArgumentNullException(nameof(stderr));
        }

        // Installed gate — short-circuit before querying so the SCM is never probed for
        // a service that doesn't exist.
        if (!controller.Exists(serviceName))
        {
            stdout.WriteLine($"Service '{serviceName}' is not installed.");
            return StatusFormatter.ExitCode(ServiceState.NotInstalled);
        }

        var info = controller.Query(serviceName);
        stdout.Write(StatusFormatter.Format(info, clock));
        return StatusFormatter.ExitCode(info.State);
    }
}
