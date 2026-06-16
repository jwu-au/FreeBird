using System;

namespace FreeBird.Cli.Service;

/// <summary>
/// Cross-platform fallback <see cref="IEventLogWriter"/> that throws
/// <see cref="PlatformNotSupportedException"/> from every member. Used on
/// non-Windows hosts so <c>fb service install</c>/<c>uninstall</c> emit a clean,
/// friendly error instead of attempting Windows-only Event Log calls.
///
/// OS selection happens at DI-binding time in <c>CliServiceModule</c> (T14);
/// this type itself is unconditional by design — do NOT call
/// <c>OperatingSystem.IsWindows()</c> here. Because it never references
/// <c>System.Diagnostics.EventLog</c>, it compiles on every OS and stays in the
/// normal compile set (unlike <c>WindowsEventLogWriter</c>).
/// </summary>
public sealed class NotSupportedEventLogWriter : IEventLogWriter
{
    private const string NotSupportedMessage =
        "'fb service' is Windows-only. See README §Service for setup on this platform.";

    /// <inheritdoc/>
    public void CreateSourceIfMissing(string source, string logName)
        => throw new PlatformNotSupportedException(NotSupportedMessage);

    /// <inheritdoc/>
    public void RemoveSourceIfExists(string source)
        => throw new PlatformNotSupportedException(NotSupportedMessage);
}
