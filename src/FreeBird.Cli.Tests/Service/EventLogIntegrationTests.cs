using System;
using System.Diagnostics;
using FluentAssertions;
using FreeBird.Cli.Service;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T24 — Windows-only integration tests for the Event Log source lifecycle through
/// <see cref="WindowsEventLogWriter"/> (create on install, remove on uninstall).
///
/// This file is EXCLUDED from the macOS/Linux compile set (see
/// <c>FreeBird.Cli.Tests.csproj</c>) because it references
/// <c>System.Diagnostics.EventLog</c> and the Windows-only
/// <see cref="WindowsEventLogWriter"/> type, both of which compile on Windows only
/// (their backing NuGet is restored on Windows alone — T01). On Windows CI the file
/// compiles and runs; the per-test <c>Skip.IfNot(OperatingSystem.IsWindows())</c> and
/// <c>if (OperatingSystem.IsWindows())</c> body guard are belt-and-suspenders.
///
/// Each test uses a unique source name <c>FreeBird_Test_{Guid:N}</c> so concurrent
/// runs never collide, and <see cref="Dispose"/> deletes that source so runs do not
/// leak Event Log registrations. Creating/deleting an event source requires
/// Administrator privileges; CI agents for this project run elevated.
/// </summary>
[Trait("os", "windows")]
public sealed class EventLogIntegrationTests : IDisposable
{
    private readonly string _source = $"FreeBird_Test_{Guid.NewGuid():N}";
    private const string LogName = "Application";

    [SkippableFact]
    public void CreateSourceIfMissing_RegistersSource()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Event Log integration requires Windows");

        if (OperatingSystem.IsWindows())
        {
            var sut = new WindowsEventLogWriter();

            sut.CreateSourceIfMissing(_source, LogName);

            EventLog.SourceExists(_source)
                .Should().BeTrue("the source must exist after CreateSourceIfMissing");
        }
    }

    [SkippableFact]
    public void CreateSourceIfMissing_SecondCall_IsIdempotent()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Event Log integration requires Windows");

        if (OperatingSystem.IsWindows())
        {
            var sut = new WindowsEventLogWriter();
            sut.CreateSourceIfMissing(_source, LogName);

            Action secondCall = () => sut.CreateSourceIfMissing(_source, LogName);

            secondCall.Should().NotThrow("a second CreateSourceIfMissing must be a no-op");
            EventLog.SourceExists(_source)
                .Should().BeTrue("the source must still exist after the idempotent second call");
        }
    }

    [SkippableFact]
    public void RemoveSourceIfExists_CleansUp_AndSecondCallIsNoOp()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Event Log integration requires Windows");

        if (OperatingSystem.IsWindows())
        {
            var sut = new WindowsEventLogWriter();
            sut.CreateSourceIfMissing(_source, LogName);

            sut.RemoveSourceIfExists(_source);
            EventLog.SourceExists(_source)
                .Should().BeFalse("the source must be gone after RemoveSourceIfExists");

            Action secondRemove = () => sut.RemoveSourceIfExists(_source);
            secondRemove.Should().NotThrow("removing an already-absent source must be a no-op");
            EventLog.SourceExists(_source)
                .Should().BeFalse("the source must remain absent after the second remove");
        }
    }

    [SkippableFact]
    public void Dispose_DeletesLingeringSource_BestEffort()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Event Log integration requires Windows");

        if (OperatingSystem.IsWindows())
        {
            // Create a separate, locally-scoped source and prove that the same best-effort
            // delete logic Dispose uses removes it without throwing.
            var lingering = $"FreeBird_Test_{Guid.NewGuid():N}";
            var sut = new WindowsEventLogWriter();
            sut.CreateSourceIfMissing(lingering, LogName);
            EventLog.SourceExists(lingering).Should().BeTrue("setup must register the lingering source");

            Action bestEffortDelete = () =>
            {
                try
                {
                    if (EventLog.SourceExists(lingering))
                    {
                        EventLog.DeleteEventSource(lingering);
                    }
                }
                catch
                {
                    // Swallow — best-effort cleanup must never throw.
                }
            };

            bestEffortDelete.Should().NotThrow("best-effort Dispose-style cleanup must swallow failures");
            EventLog.SourceExists(lingering)
                .Should().BeFalse("the lingering source must be deleted by the best-effort cleanup");
        }
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
