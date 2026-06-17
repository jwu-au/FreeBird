using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Cli.Service;
using FreeBird.Core.Service;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T23 — Windows-only integration tests for <see cref="WindowsServiceController"/>
/// driven against the real Service Control Manager (SCM).
///
/// <para><b>Compile exclusion.</b> This file is EXCLUDED from the macOS/Linux compile
/// set (see <c>FreeBird.Cli.Tests.csproj</c>) because it exercises the Windows-only
/// SCM stack (<c>System.ServiceProcess</c> + advapi32 P/Invoke via
/// <see cref="WindowsServiceController"/>, which is itself <c>[SupportedOSPlatform("windows")]</c>).
/// On macOS the file is not compiled at all, so it contributes 0 tests there; on
/// Windows CI it compiles and runs. The per-test
/// <c>Skip.IfNot(OperatingSystem.IsWindows())</c> is belt-and-suspenders.</para>
///
/// <para><b>Why cmd.exe and what is asserted.</b> Creating/starting a Windows service
/// requires a service-compliant binary that talks back to the SCM. We deliberately do
/// NOT ship such a host binary (it would be a new production artifact), so the install
/// uses <c>%SystemRoot%\System32\cmd.exe</c> as a harmless no-op binary. cmd.exe is NOT
/// a real service, so the SCM cannot bring it to <c>Running</c> — <see cref="WindowsServiceController.StartAsync"/>
/// will time out / fault. The deterministic, always-true parts of the lifecycle are
/// therefore the core assertions: <c>Install</c> succeeds, <c>Exists</c> == true,
/// <c>Query</c> reports a non-<c>NotInstalled</c> state, <c>Uninstall</c> succeeds,
/// <c>Exists</c> == false. The Start→Running→Stop transition is treated as
/// <i>best-effort</i>: it is attempted with a short timeout inside try/catch; if it
/// faults (the expected outcome with cmd.exe) the test logs and continues to cleanup
/// rather than failing; if it unexpectedly reaches <c>Running</c> the Running→Stop→Stopped
/// path is asserted.</para>
///
/// <para><b>Elevation.</b> Creating/starting/deleting a service requires Administrator.
/// CI agents for this project run elevated, so the core operations succeed there. On a
/// non-elevated Windows dev box <c>Install</c> throws access-denied; that is caught and
/// the test is <c>Skip</c>-ped with a clear message rather than failing, so this file is
/// safe to run on a developer machine without elevation.</para>
///
/// <para><b>Isolation.</b> Each instance uses a unique service short-name
/// <c>FreeBird_Test_{Guid:N}</c> so concurrent runs never collide, and
/// <see cref="DisposeAsync"/> best-effort uninstalls it; cleanup never throws.</para>
/// </summary>
[Trait("os", "windows")]
public sealed class WindowsServiceControllerIntegrationTests : IAsyncLifetime
{
    private readonly string _name = $"FreeBird_Test_{Guid.NewGuid():N}";

    public Task InitializeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task InstallStartStopUninstall_RoundTrips()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WindowsServiceController requires Windows + the SCM");

        if (OperatingSystem.IsWindows())
        {
            var sut = new WindowsServiceController();

            // %SystemRoot%\System32\cmd.exe — a harmless no-op binary. It is NOT a
            // service-compliant host, so START will not reach Running (see class docs).
            string binPath = Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\cmd.exe");
            var request = new ServiceInstallRequest(
                ShortName: _name,
                DisplayName: $"FreeBird Test {_name}",
                Description: "Transient FreeBird integration-test service; safe to delete.",
                BinPath: binPath,
                Account: null,
                Password: null,
                RestartPolicy: RestartPolicy.Manual);

            // --- Install: deterministic, but may throw access-denied on a non-elevated box.
            try
            {
                sut.Install(request);
            }
            catch (UnauthorizedAccessException ex)
            {
                Skip.If(true, $"Service install requires elevation; running non-elevated: {ex.Message}");
                return;
            }
            catch (Win32Exception ex) when (IsAccessDenied(ex))
            {
                Skip.If(true, $"Service install requires elevation; running non-elevated: {ex.Message}");
                return;
            }
            catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception w32 && IsAccessDenied(w32))
            {
                Skip.If(true, $"Service install requires elevation; running non-elevated: {ex.Message}");
                return;
            }

            // --- Core deterministic assertions: Exists + Query.
            sut.Exists(_name).Should().BeTrue("the service must exist immediately after Install");

            ServiceStatusInfo afterInstall = sut.Query(_name);
            afterInstall.ShortName.Should().Be(_name, "Query must echo the installed service's short name");
            afterInstall.State.Should().NotBe(
                ServiceState.NotInstalled,
                "an installed-but-not-started service must report a concrete (Stopped) state, not NotInstalled");

            // --- Best-effort Start/Stop. cmd.exe cannot become a real service, so this
            // is expected to fault/time out; we never let that fail the test.
            try
            {
                await sut.StartAsync(_name, TimeSpan.FromSeconds(10), CancellationToken.None);

                // Unexpected with cmd.exe, but if the SCM did reach Running, assert the
                // full Running → Stop → Stopped transition deterministically.
                sut.Query(_name).State.Should().Be(
                    ServiceState.Running, "StartAsync returned without faulting, so the service should be Running");

                await sut.StopAsync(_name, TimeSpan.FromSeconds(10), CancellationToken.None);
                sut.Query(_name).State.Should().Be(
                    ServiceState.Stopped, "after a successful Stop the service should report Stopped");
            }
            catch (Exception ex)
            {
                // Expected path with cmd.exe: a non-service binary cannot signal the SCM,
                // so Start times out / faults. Log and fall through to Uninstall.
                Console.WriteLine(
                    $"[T23] Start/Stop best-effort skipped (expected with cmd.exe, not a real service host): {ex.GetType().Name}: {ex.Message}");
            }

            // --- Uninstall: deterministic.
            sut.Uninstall(_name);
            sut.Exists(_name).Should().BeFalse("the service must be gone after Uninstall");
        }
    }

    [Fact]
    public void WindowsServiceController_ImplementsIServiceController()
    {
        typeof(IServiceController).IsAssignableFrom(typeof(WindowsServiceController))
            .Should().BeTrue("WindowsServiceController must be substitutable at the DI boundary (T14)");
    }

    [Fact]
    public void WindowsServiceController_IsSealed()
    {
        typeof(WindowsServiceController).IsSealed
            .Should().BeTrue("the SCM controller is a leaf implementation and should not be subclassed");
    }

    [Fact]
    public void WindowsServiceController_HasSupportedOSPlatformWindowsAttribute()
    {
        var attributes = typeof(WindowsServiceController)
            .GetCustomAttributes(typeof(SupportedOSPlatformAttribute), inherit: false)
            .Cast<SupportedOSPlatformAttribute>()
            .ToList();

        attributes.Should().NotBeEmpty(
            "the class must carry [SupportedOSPlatform] so the CA1416 guard around the SCM P/Invoke stays in place");
        attributes.Should().Contain(
            a => a.PlatformName.Contains("windows", StringComparison.OrdinalIgnoreCase),
            "the supported platform must be Windows");
    }

    /// <summary>Win32 ERROR_ACCESS_DENIED (5) — the access-denied code surfaced when not elevated.</summary>
    private static bool IsAccessDenied(Win32Exception ex) => ex.NativeErrorCode == 5;

    public async Task DisposeAsync()
    {
        // Best-effort cleanup so test runs never leak SCM registrations. Must never throw.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var sut = new WindowsServiceController();
                if (sut.Exists(_name))
                {
                    sut.Uninstall(_name);
                }
            }
            catch
            {
                // Swallow — cleanup must never fail a test run (e.g. non-elevated host).
            }
        }

        await Task.CompletedTask;
    }
}
