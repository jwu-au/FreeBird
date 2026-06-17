using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Service;
using Serilog;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// T22 — the hidden <c>fb service run</c> SCM entrypoint. It is invoked by the Windows Service
/// Control Manager (never interactively) and hosts the watch loop via <c>WatchHostedService</c>.
///
/// <para>The macOS/Linux-testable surface is <see cref="HandleAsync"/> steps 1-3 (the OS guard,
/// the not-a-SCM guard, and the config-load-failure path). The actual generic-host build/run is
/// isolated in the Windows-only partial <see cref="RunHostAsync"/> (compiled on Windows only),
/// so this file builds warning-free on every OS while the end-to-end host run is validated by
/// Windows CI (T24).</para>
///
/// <para><paramref name="isWindowsService"/> is injected as a delegate (NOT a mutable static
/// seam) so tests can simulate running inside / outside the SCM. CliRoot passes
/// <c>() =&gt; OperatingSystem.IsWindows() &amp;&amp; WindowsServiceHelpers.IsWindowsService()</c>.</para>
///
/// Exit codes: 1 OS guard (non-Windows) or not-a-SCM; 3 config load failure; 0 host ran to
/// completion.
/// </summary>
public static partial class RunCommand
{
    /// <summary>
    /// The friendly message shown when a <c>service</c> leaf is invoked on a non-Windows host.
    /// Shared verbatim with the per-leaf short-circuits wired in <c>CliRoot.Build()</c>.
    /// </summary>
    internal const string WindowsOnlyMessage =
        "ERROR: 'fb service' is Windows-only. See README §Service for launchd / systemd snippets.";

    /// <summary>
    /// True when the current process was launched by the Windows Service Control Manager. The body
    /// is platform-split (Windows file calls <c>WindowsServiceHelpers.IsWindowsService()</c>; the
    /// non-Windows file returns <c>false</c>) so the Windows-only NuGet is referenced on Windows
    /// only. CliRoot passes this method as the <c>isWindowsService</c> delegate to
    /// <see cref="HandleAsync"/>.
    /// </summary>
    public static partial bool IsRunningAsWindowsService();

    public static async Task<int> HandleAsync(
        string configPath,
        Func<bool> isWindowsService,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(isWindowsService);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        // 1. Non-Windows short-circuit — FIRST, before touching any Windows-only host code.
        if (!OperatingSystem.IsWindows())
        {
            stderr.WriteLine(WindowsOnlyMessage);
            return 1;
        }

        // 2. SCM gate — `run` is the SCM entrypoint; refuse interactive use.
        if (!isWindowsService())
        {
            stderr.WriteLine(
                "ERROR: 'fb service run' is invoked by the Service Control Manager. " +
                "Use 'fb watch' for interactive use.");
            return 1;
        }

        // 3. Load + validate config BEFORE building/starting the host so a bad config fails fast
        //    without ever registering a hosted service.
        using var loadLogger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();

        RootConfig config;
        try
        {
            config = new JsonConfigLoader(loadLogger).Load(configPath);
        }
        catch (ConfigLoadException ex)
        {
            stderr.WriteLine(
                $"Config error in '{configPath}' (field '{ex.FieldName}'): {ex.Message}");
            return 3;
        }
        catch (FileNotFoundException)
        {
            stderr.WriteLine($"Config file not found: {configPath}");
            return 3;
        }

        // 4. Build and run the Windows generic host. Isolated in a Windows-only partial so the
        //    Windows-only UseWindowsService() / EventLog sink compile only on Windows. Reached
        //    only after the OperatingSystem.IsWindows() guard above.
        return await RunHostAsync(config, configPath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds and runs the generic host that drives the watch loop under the SCM. The real body
    /// lives in the Windows-only partial <c>RunCommand.Windows.cs</c> (compiled on Windows only);
    /// on macOS / Linux this fallback is compiled instead and is never reached because
    /// <see cref="HandleAsync"/> returns at the OS guard before calling it.
    /// </summary>
    private static partial Task<int> RunHostAsync(RootConfig config, string configPath, CancellationToken ct);
}
