using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli.Commands.Service;

namespace FreeBird.Cli.Tests.Commands.Service;

/// <summary>
/// T22 — tests the macOS-testable surface of <see cref="RunCommand.HandleAsync"/>: the OS guard
/// (non-Windows short-circuit) and, on Windows CI only, the not-a-SCM and config-load-failure
/// branches. The actual IHost run is NOT exercised here — it is validated by Windows CI (T24).
/// </summary>
public class RunCommandTests
{
    private const string WindowsOnlyMarker = "Windows-only";

    [Fact]
    public async Task Run_InvokedOutsideSCM_ExitsWithError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await RunCommand.HandleAsync(
            configPath: "cfg.json",
            isWindowsService: () => false,
            stdout: stdout,
            stderr: stderr,
            ct: CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            // On macOS/Linux the OS guard trips FIRST: exit 1 + the friendly Windows-only message.
            exit.Should().Be(1);
            stderr.ToString().Should().Contain(WindowsOnlyMarker);
        }
        else
        {
            // On Windows the OS guard passes; isWindowsService() == false trips the not-a-SCM
            // branch with a non-zero exit and a hint to use `fb watch`.
            exit.Should().NotBe(0);
            stderr.ToString().Should().Contain("Service Control Manager");
        }
    }

    [Fact]
    public async Task Run_ConfigLoadFailure_DoesNotStartHost()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var missingConfig = Path.Combine(
            Path.GetTempPath(), $"fb-missing-{Guid.NewGuid():N}.json");

        var exit = await RunCommand.HandleAsync(
            configPath: missingConfig,
            isWindowsService: () => true,
            stdout: stdout,
            stderr: stderr,
            ct: CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            // OS guard trips before config loading on non-Windows.
            exit.Should().Be(1);
            stderr.ToString().Should().Contain(WindowsOnlyMarker);
        }
        else
        {
            // On Windows: passes OS + SCM guards, then fails to load the missing config with a
            // non-zero exit BEFORE any host is built.
            exit.Should().NotBe(0);
        }
    }
}
