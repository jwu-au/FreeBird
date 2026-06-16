using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using FluentAssertions;
using FreeBird.Cli.Service;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T11 — Windows-only smoke + round-trip tests for <see cref="WindowsEventLogWriter"/>.
///
/// This file is EXCLUDED from the macOS/Linux compile set (see
/// <c>FreeBird.Cli.Tests.csproj</c>) because it references
/// <c>System.Diagnostics.EventLog</c>, whose backing NuGet is restored on Windows
/// only (T01). On Windows CI the file compiles and runs; the per-test
/// <c>Skip.IfNot(OperatingSystem.IsWindows())</c> is belt-and-suspenders.
///
/// Each test uses a unique source name <c>FreeBird_Test_{Guid:N}</c> so concurrent
/// runs never collide, and <see cref="Dispose"/> deletes that source so runs do
/// not leak Event Log registrations.
///
/// Creating/deleting an event source requires Administrator privileges. CI agents
/// for this project run elevated, so create/remove succeed there. The non-admin
/// "requires Administrator" wrapped-exception path cannot be asserted
/// deterministically (it depends on elevation state), so it is verified by the
/// manual smoke in T18/T19 rather than a flaky unit test — see the T11 commit body.
/// </summary>
[Trait("os", "windows")]
public sealed class WindowsEventLogWriterTests : IDisposable
{
    private readonly string _source = $"FreeBird_Test_{Guid.NewGuid():N}";
    private const string LogName = "Application";

    [SkippableFact]
    public void CreateSourceIfMissing_IsIdempotent()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WindowsEventLogWriter requires Windows");

        if (OperatingSystem.IsWindows())
        {
            var sut = new WindowsEventLogWriter();

            sut.CreateSourceIfMissing(_source, LogName);
            Action secondCall = () => sut.CreateSourceIfMissing(_source, LogName);

            secondCall.Should().NotThrow("CreateSourceIfMissing must be idempotent");
            EventLog.SourceExists(_source).Should().BeTrue("the source must exist after creation");
        }
    }

    [SkippableFact]
    public void RemoveSourceIfExists_IsIdempotent()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WindowsEventLogWriter requires Windows");

        if (OperatingSystem.IsWindows())
        {
            var sut = new WindowsEventLogWriter();

            // Removing a non-existent source must not throw.
            Action removeMissing = () => sut.RemoveSourceIfExists(_source);
            removeMissing.Should().NotThrow("removing a non-existent source must be a no-op");

            sut.CreateSourceIfMissing(_source, LogName);
            sut.RemoveSourceIfExists(_source);

            EventLog.SourceExists(_source).Should().BeFalse("the source must be gone after removal");
        }
    }

    [SkippableFact]
    public void CreateThenRemove_RoundTrips()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WindowsEventLogWriter requires Windows");

        if (OperatingSystem.IsWindows())
        {
            var sut = new WindowsEventLogWriter();

            sut.CreateSourceIfMissing(_source, LogName);
            EventLog.SourceExists(_source).Should().BeTrue("source must exist after create");

            sut.RemoveSourceIfExists(_source);
            EventLog.SourceExists(_source).Should().BeFalse("source must be gone after remove");
        }
    }

    [Fact]
    public void WindowsEventLogWriter_ImplementsIEventLogWriter()
    {
        typeof(IEventLogWriter).IsAssignableFrom(typeof(WindowsEventLogWriter))
            .Should().BeTrue("WindowsEventLogWriter must be substitutable at the DI boundary in T14");
    }

    [Fact]
    public void WindowsEventLogWriter_IsSealed()
    {
        typeof(WindowsEventLogWriter).IsSealed
            .Should().BeTrue("the event-log writer is a leaf implementation and should not be subclassed");
    }

    [Fact]
    public void WindowsEventLogWriter_HasSupportedOSPlatformWindowsAttribute()
    {
        var attributes = typeof(WindowsEventLogWriter)
            .GetCustomAttributes(typeof(SupportedOSPlatformAttribute), inherit: false)
            .Cast<SupportedOSPlatformAttribute>()
            .ToList();

        attributes.Should().NotBeEmpty(
            "the class must carry [SupportedOSPlatform] so the CA1416 guard around System.Diagnostics.EventLog stays in place");
        attributes.Should().Contain(
            a => a.PlatformName.Contains("windows", StringComparison.OrdinalIgnoreCase),
            "the supported platform must be Windows");
    }

    public void Dispose()
    {
        // Best-effort cleanup so test runs do not leak Event Log sources.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (EventLog.SourceExists(_source))
                {
                    EventLog.DeleteEventSource(_source);
                }
            }
            catch
            {
                // Swallow — cleanup must never fail a test run (e.g. non-elevated host).
            }
        }
    }
}
