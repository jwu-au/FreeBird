using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;

namespace FreeBird.Cli.Service;

/// <summary>
/// Production <see cref="IEventLogWriter"/> that creates/removes the Windows Event
/// Log source at install/uninstall time via <see cref="EventLog"/>.
///
/// Used as the Windows arm of the OS-conditional binding in <c>CliServiceModule</c>
/// (T14): on Windows this overrides <see cref="NotSupportedEventLogWriter"/> so the
/// install handler can register the source and the uninstall handler can remove it.
///
/// This writer holds NO state and NO native handle (M-5) — it is a thin, stateless
/// wrapper over the static <see cref="EventLog"/> APIs and is therefore NOT
/// <see cref="IDisposable"/>. At runtime the service logs through
/// <c>Serilog.Sinks.EventLog</c> (a separate handle), so there is no
/// <c>Write(...)</c> method here.
/// </summary>
/// <remarks>
/// The whole type is marked <c>[SupportedOSPlatform("windows")]</c> because
/// <see cref="EventLog"/> comes from the <c>System.Diagnostics.EventLog</c> NuGet,
/// which is restored on Windows only (T01). The source file is additionally
/// excluded from the macOS/Linux compile set in <c>FreeBird.Cli.csproj</c> — on
/// non-Windows hosts the type does not exist and the DI binding selects
/// <see cref="NotSupportedEventLogWriter"/> instead.
///
/// Creating or deleting an event source requires Administrator privileges. The
/// underlying APIs surface this as <see cref="SecurityException"/> /
/// <see cref="UnauthorizedAccessException"/>; both are rethrown as an
/// <see cref="InvalidOperationException"/> whose message contains the literal
/// "requires Administrator" so callers (T18/T19) can present a clear remedy.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsEventLogWriter : IEventLogWriter
{
    /// <inheritdoc/>
    public void CreateSourceIfMissing(string source, string logName)
    {
        try
        {
            if (!EventLog.SourceExists(source))
            {
                EventLog.CreateEventSource(new EventSourceCreationData(source, logName));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Creating the Event Log source '{source}' requires Administrator privileges.", ex);
        }
    }

    /// <inheritdoc/>
    public void RemoveSourceIfExists(string source)
    {
        try
        {
            if (EventLog.SourceExists(source))
            {
                EventLog.DeleteEventSource(source);
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Removing the Event Log source '{source}' requires Administrator privileges.", ex);
        }
    }
}
