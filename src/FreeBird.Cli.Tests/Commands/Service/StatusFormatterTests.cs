using System;
using FluentAssertions;
using FreeBird.Cli.Commands.Service;
using FreeBird.Cli.Service;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreeBird.Cli.Tests.Commands.Service;

/// <summary>
/// T16 — Unit tests for <see cref="StatusFormatter"/>: the pure (no-I/O) renderer of the
/// design §2.8 <c>fb service status</c> block, plus uptime formatting and exit-code mapping.
///
/// Timestamp formatting decision: <see cref="StatusFormatter"/> formats
/// <see cref="ServiceStatusInfo.StartedAtUtc"/> via <c>.UtcDateTime</c> using
/// <c>CultureInfo.InvariantCulture</c>, so the rendered "started" string is machine-TZ
/// independent. Tests therefore construct <c>StartedAtUtc</c> as a UTC instant.
/// </summary>
public class StatusFormatterTests
{
    [Fact]
    public void Format_RunningState_RendersExactSpecBlock()
    {
        // StartedAtUtc chosen so the .UtcDateTime renders "2026-06-15 12:34:56".
        var startedAtUtc = new DateTimeOffset(2026, 6, 15, 12, 34, 56, TimeSpan.Zero);
        var info = new ServiceStatusInfo(
            ShortName: "FreeBird",
            DisplayName: "FreeBird Music Cache Decoder",
            State: ServiceState.Running,
            Account: "LocalSystem",
            StartType: "Auto",
            StartedAtUtc: startedAtUtc,
            ConfigPath: "C:\\ProgramData\\FreeBird\\config.json",
            LogFilePath: "C:\\ProgramData\\FreeBird\\logs\\watch-2026-06-15.log");

        var clock = new FakeTimeProvider();
        // Now = started + 2h14m → uptime "2h 14m".
        clock.SetUtcNow(startedAtUtc + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(14));

        var expected =
            "Service:     FreeBird\n" +
            "Display:     FreeBird Music Cache Decoder\n" +
            "Status:      Running       (started 2026-06-15 12:34:56, uptime 2h 14m)\n" +
            "Account:     LocalSystem\n" +
            "Startup:     Automatic\n" +
            "Config:      C:\\ProgramData\\FreeBird\\config.json\n" +
            "Log file:    C:\\ProgramData\\FreeBird\\logs\\watch-2026-06-15.log\n";

        var actual = StatusFormatter.Format(info, clock).Replace("\r\n", "\n");

        actual.Should().Be(expected);
    }

    [Fact]
    public void Format_StoppedState_StatusLineHasNoParenthetical()
    {
        var info = new ServiceStatusInfo(
            ShortName: "FreeBird",
            DisplayName: "FreeBird Music Cache Decoder",
            State: ServiceState.Stopped,
            Account: "LocalSystem",
            StartType: "Auto",
            StartedAtUtc: null,
            ConfigPath: "C:\\ProgramData\\FreeBird\\config.json",
            LogFilePath: "C:\\ProgramData\\FreeBird\\logs\\watch-2026-06-15.log");

        var clock = new FakeTimeProvider();

        var actual = StatusFormatter.Format(info, clock).Replace("\r\n", "\n");

        actual.Should().Contain("Status:      Stopped\n");
        actual.Should().NotContain("(started");
    }

    [Fact]
    public void Format_NullConfigAndLogFile_RendersNonePlaceholder()
    {
        var info = new ServiceStatusInfo(
            ShortName: "FreeBird",
            DisplayName: "FreeBird Music Cache Decoder",
            State: ServiceState.Stopped,
            Account: "LocalSystem",
            StartType: "Auto",
            StartedAtUtc: null,
            ConfigPath: null,
            LogFilePath: null);

        var clock = new FakeTimeProvider();

        var actual = StatusFormatter.Format(info, clock).Replace("\r\n", "\n");

        actual.Should().Contain("Config:      (none)\n");
        actual.Should().Contain("Log file:    (none)\n");
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(90, "1m 30s")]
    [InlineData(30, "30s")]
    public void FormatUptime_Seconds_FormatsExpected(int seconds, string expected)
    {
        StatusFormatter.FormatUptime(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void FormatUptime_HoursAndMinutes_FormatsExpected()
    {
        var uptime = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(14);
        StatusFormatter.FormatUptime(uptime).Should().Be("2h 14m");
    }

    [Fact]
    public void FormatUptime_DaysAndHours_FormatsExpected()
    {
        var uptime = TimeSpan.FromDays(3) + TimeSpan.FromHours(2);
        StatusFormatter.FormatUptime(uptime).Should().Be("3d 2h");
    }

    [Theory]
    [InlineData(ServiceState.Running, 0)]
    [InlineData(ServiceState.NotInstalled, 1)]
    [InlineData(ServiceState.Stopped, 2)]
    [InlineData(ServiceState.Paused, 3)]
    [InlineData(ServiceState.StartPending, 3)]
    [InlineData(ServiceState.StopPending, 3)]
    [InlineData(ServiceState.Other, 3)]
    public void ExitCode_MapsStateToCode(ServiceState state, int expected)
    {
        StatusFormatter.ExitCode(state).Should().Be(expected);
    }
}
