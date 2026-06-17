using System;
using System.Globalization;
using FreeBird.Cli.Service;

namespace FreeBird.Cli.Commands.Service;

/// <summary>
/// Pure (no-I/O) renderer of the design §2.8 <c>fb service status</c> block, plus the
/// uptime-formatting and exit-code-mapping helpers it depends on.
///
/// Layout (measured byte-for-byte from §2.8):
///   - Each line is <c>&lt;label&gt;&lt;value&gt;</c> where the label is left-justified and
///     padded to width 13, so every value column begins at offset 13. (e.g. <c>"Service:"</c>
///     is 8 chars + 5 spaces; <c>"Log file:"</c> is 9 chars + 4 spaces — both reach 13.)
///   - On the Status line the state word is itself left-justified and padded to width 14
///     before the parenthetical, so <c>"Running"</c> (7 chars) is followed by 7 spaces then
///     <c>"("</c> — matching the spec's literal <c>"Running       ("</c>.
///   - Lines are joined with a literal "\n" (not Environment.NewLine) and the block ends
///     with a trailing "\n", so the rendered output is identical on every OS.
///
/// Timestamp formatting: <see cref="ServiceStatusInfo.StartedAtUtc"/> is rendered via
/// <c>.UtcDateTime</c> with <see cref="CultureInfo.InvariantCulture"/> so the "started"
/// string is machine-timezone independent.
/// </summary>
public static class StatusFormatter
{
    private const int LabelWidth = 13;
    private const int StateWidth = 14;

    /// <summary>
    /// Renders the full §2.8 status block for <paramref name="info"/>, computing uptime as
    /// <c><paramref name="clock"/>.GetUtcNow() - info.StartedAtUtc</c> when the service is
    /// running and a start timestamp is present.
    /// </summary>
    public static string Format(ServiceStatusInfo info, TimeProvider clock)
    {
        var serviceLine = Line("Service:", info.ShortName);
        var displayLine = Line("Display:", info.DisplayName);
        var statusLine = Line("Status:", FormatStatusValue(info, clock));
        var accountLine = Line("Account:", info.Account ?? "Unknown");
        var startupLine = Line("Startup:", FormatStartType(info.StartType));
        var configLine = Line("Config:", info.ConfigPath ?? "(none)");
        var logLine = Line("Log file:", info.LogFilePath ?? "(none)");

        return string.Join(
            "\n",
            serviceLine,
            displayLine,
            statusLine,
            accountLine,
            startupLine,
            configLine,
            logLine) + "\n";
    }

    /// <summary>
    /// Formats <paramref name="uptime"/> as the two most-significant non-zero units in
    /// d/h/m/s order: days→<c>"{d}d {h}h"</c>, else hours→<c>"{h}h {m}m"</c>, else
    /// minutes→<c>"{m}m {s}s"</c>, else <c>"{s}s"</c>.
    /// </summary>
    public static string FormatUptime(TimeSpan uptime)
    {
        var days = (int)uptime.TotalDays;
        var hours = uptime.Hours;
        var minutes = uptime.Minutes;
        var seconds = uptime.Seconds;

        if (days > 0)
        {
            return $"{days}d {hours}h";
        }

        if (hours > 0)
        {
            return $"{hours}h {minutes}m";
        }

        if (minutes > 0)
        {
            return $"{minutes}m {seconds}s";
        }

        return $"{seconds}s";
    }

    /// <summary>
    /// Maps a <see cref="ServiceState"/> to the process exit code used by <c>fb service status</c>:
    /// Running→0, NotInstalled→1, Stopped→2, everything else (Paused / StartPending /
    /// StopPending / Other)→3.
    /// </summary>
    public static int ExitCode(ServiceState state)
    {
        return state switch
        {
            ServiceState.Running => 0,
            ServiceState.NotInstalled => 1,
            ServiceState.Stopped => 2,
            _ => 3,
        };
    }

    private static string Line(string label, string value)
    {
        return $"{label,-LabelWidth}{value}";
    }

    private static string FormatStatusValue(ServiceStatusInfo info, TimeProvider clock)
    {
        var state = info.State.ToString();

        if (info.State != ServiceState.Running || info.StartedAtUtc is null)
        {
            return state;
        }

        var startedAtUtc = info.StartedAtUtc.Value;
        var started = startedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var uptime = FormatUptime(clock.GetUtcNow() - startedAtUtc);

        return $"{state,-StateWidth}(started {started}, uptime {uptime})";
    }

    private static string FormatStartType(string? startType)
    {
        return startType switch
        {
            null => "Unknown",
            "Auto" => "Automatic",
            "AutoDelayedStart" => "Automatic (Delayed Start)",
            _ => startType,
        };
    }
}
