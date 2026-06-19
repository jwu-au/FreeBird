using System.IO;
using FluentAssertions;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// Locks the Windows Event Log sink minimum level for the <c>fb service run</c> host.
///
/// The service routes Serilog to TWO sinks (see <c>RunCommand.Windows.cs</c>): a rolling
/// FILE sink at <c>Information</c> (full detail under
/// <c>C:\ProgramData\FreeBird\logs</c>) and the Windows EVENT LOG sink, which is
/// deliberately restricted to <c>Error</c> and above so the Application event log stays
/// focused on actionable failures rather than routine warnings.
///
/// The sink is configured inline in the Windows-only host body, which needs a full
/// Windows Service host + <c>WatchSupervisor</c> to boot, so it cannot be exercised by a
/// plain unit test. This is therefore a SOURCE-CONTRACT test (same style as
/// <c>CsprojDependenciesTests</c>): it asserts the source text directly so an accidental
/// regression to <c>Warning</c> (or removal of the level restriction) is caught
/// immediately and cross-OS, rather than only being noticed on a running Windows box.
/// </summary>
public class EventLogSinkLevelTests
{
    private static string LoadRunCommandWindowsSource()
    {
        // Walk up from the test working directory to the repo root (dir containing
        // FreeBird.sln), mirroring CsprojDependenciesTests, so the test is independent
        // of where the runner places the working directory.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FreeBird.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("repo root containing FreeBird.sln must be discoverable from the test working directory");

        var sourcePath = Path.Combine(
            dir!.FullName, "src", "FreeBird.Cli", "Commands", "Service", "RunCommand.Windows.cs");
        File.Exists(sourcePath).Should().BeTrue($"the Windows host body should exist at {sourcePath}");

        return File.ReadAllText(sourcePath);
    }

    [Fact]
    public void EventLogSink_IsRestrictedToErrorLevel()
    {
        var source = LoadRunCommandWindowsSource();

        source.Should().Contain(
            "restrictedToMinimumLevel: LogEventLevel.Error",
            "the Windows Event Log sink must only receive Error and above (warnings stay in the file log)");
    }

    [Fact]
    public void EventLogSink_DoesNotEmitWarningsToEventLog()
    {
        var source = LoadRunCommandWindowsSource();

        source.Should().NotContain(
            "restrictedToMinimumLevel: LogEventLevel.Warning",
            "warnings must NOT be routed to the Windows Event Log (regression guard against the old Warning threshold)");
    }
}
