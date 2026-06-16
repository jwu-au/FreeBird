using System;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using FreeBird.Cli.Service;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T10 — Contract + (Windows-only) smoke tests for <see cref="WindowsElevationChecker"/>.
///
/// The real admin check requires running in an elevated / non-elevated process
/// context, so it cannot be unit-tested deterministically on a build host. The
/// behaviour is therefore covered indirectly via a mocked <see cref="IElevationChecker"/>
/// in the install/uninstall handler tests (T18/T19). What we CAN lock down here,
/// on every OS, is the type's shape:
///
///   (1) it implements <see cref="IElevationChecker"/> so it is substitutable at
///       the DI boundary (wiring in T14);
///   (2) it is <c>sealed</c> to lock down extensibility;
///   (3) it carries <c>[SupportedOSPlatform("windows")]</c> so the CA1416
///       platform-compatibility guard around <c>WindowsIdentity</c> stays in place —
///       a future edit that drops the attribute fails this test;
///   (4) on Windows only, <c>IsAdmin()</c> can be invoked without throwing and
///       returns a <see cref="bool"/> (the actual value depends on CI elevation).
///
/// Tests (1)-(3) are pure reflection over <c>typeof(WindowsElevationChecker)</c> —
/// no instantiation — so they never trip CA1416 on macOS/Linux. Test (4) guards
/// instantiation behind an <c>OperatingSystem.IsWindows()</c> if-block, which the
/// analyzer recognises as a platform guard.
/// </summary>
public class WindowsElevationCheckerTests
{
    [Fact]
    public void WindowsElevationChecker_ImplementsIElevationChecker()
    {
        typeof(IElevationChecker).IsAssignableFrom(typeof(WindowsElevationChecker))
            .Should().BeTrue("WindowsElevationChecker must be substitutable for IElevationChecker at the DI boundary");
    }

    [Fact]
    public void WindowsElevationChecker_IsSealed()
    {
        typeof(WindowsElevationChecker).IsSealed
            .Should().BeTrue("the elevation checker is a leaf implementation and should not be subclassed");
    }

    [Fact]
    public void WindowsElevationChecker_HasSupportedOSPlatformWindowsAttribute()
    {
        var attributes = typeof(WindowsElevationChecker)
            .GetCustomAttributes(typeof(SupportedOSPlatformAttribute), inherit: false)
            .Cast<SupportedOSPlatformAttribute>()
            .ToList();

        attributes.Should().NotBeEmpty(
            "the class must carry [SupportedOSPlatform] so the CA1416 guard around WindowsIdentity stays in place");
        attributes.Should().Contain(
            a => a.PlatformName.Contains("windows", StringComparison.OrdinalIgnoreCase),
            "the supported platform must be Windows");
    }

    [SkippableFact]
    public void IsAdmin_OnWindows_DoesNotThrow_AndReturnsBool()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WindowsElevationChecker is Windows-only");

        // Guard ALL Windows-only access behind OperatingSystem.IsWindows() so CA1416
        // is satisfied at compile time on non-Windows build hosts. The call must be
        // made directly inside this if-block (not via a captured lambda) because the
        // analyzer cannot see the platform guard across a lambda body.
        if (OperatingSystem.IsWindows())
        {
            var checker = new WindowsElevationChecker();

            // The value depends on whether CI runs elevated; we only assert the
            // call completes without throwing. (Return type is bool by the
            // interface contract, so no runtime type assertion is needed.)
            var result = checker.IsAdmin();

            // Touch the result so the call is not optimised away; both values are valid.
            (result || !result).Should().BeTrue();
        }
    }
}
