using System;
using System.IO;
using System.IO.Abstractions;
using FreeBird.Core.Abstractions;

namespace FreeBird.Cli.Service;

/// <summary>
/// Mockable abstraction over the design §2.4 log-file fallback decision.
///
/// Given the operator-requested log path (from <c>service.log_file</c> /
/// <c>watch.log_file</c>), it returns the path Serilog's file sink should
/// actually use. If the requested path is unwritable (parent directory missing,
/// permission denied, or any I/O error while probing), it falls back to the
/// <c>%ProgramData%\FreeBird\logs\freebird-service-.log</c> default and reports
/// that a fallback occurred via <see cref="LogPathResolution.FellBack"/>.
///
/// The resolver is intentionally <b>pure and OS-agnostic</b>: it does NOT write
/// to the Event Log and uses no Windows-only types. Emitting the §2.4 WARNING to
/// the Event Log when a fallback happens is the caller's responsibility
/// (<c>RunCommand</c>, T22). Keeping the Event-Log concern out of here lets every
/// branch be unit-tested on any OS with a mock <see cref="IFileSystem"/>.
///
/// Per design B-5/B-6 this interface and its default implementation live in
/// <c>FreeBird.Cli</c> — never in <c>FreeBird.Core</c> — because
/// <see cref="FreeBird.Core.DependencyInjection.CoreModule"/> only assembly-scans
/// the Core assembly. Registration is therefore explicit in
/// <c>CliServiceModule</c> (T14); this interface is never auto-discovered.
/// </summary>
public interface ILogPathResolver : IDependency
{
    /// <summary>
    /// Resolves the final Serilog file-sink path for <paramref name="requestedPath"/>.
    /// A null/empty/whitespace request returns the ProgramData default with
    /// <see cref="LogPathResolution.FellBack"/> = <c>false</c> (that default is the
    /// intended behaviour, not a fallback). An unwritable request returns the same
    /// default with <see cref="LogPathResolution.FellBack"/> = <c>true</c> and a
    /// human-readable <see cref="LogPathResolution.Reason"/>.
    /// </summary>
    LogPathResolution Resolve(string? requestedPath);
}

/// <summary>
/// Outcome of <see cref="ILogPathResolver.Resolve"/>.
/// </summary>
/// <param name="FinalPath">The path Serilog should use for its file sink.</param>
/// <param name="FellBack">
/// True iff the requested path was unwritable and <see cref="FinalPath"/> is the
/// ProgramData default. The caller should emit an Event-Log WARNING when this is true.
/// </param>
/// <param name="Reason">
/// Human-readable explanation of why the fallback occurred (names the missing
/// directory or the access error), or <c>null</c> when no fallback happened.
/// </param>
public sealed record LogPathResolution(string FinalPath, bool FellBack, string? Reason);

/// <summary>
/// Default <see cref="ILogPathResolver"/> implementation.
///
/// Writability is probed by opening the requested file for append and closing it
/// immediately, via the injected <see cref="IFileSystem"/>. Note this probe
/// <b>creates the log file if the parent directory is writable</b> — that is
/// acceptable because Serilog would create the file anyway; the probe deliberately
/// does NOT delete it afterwards (deletion would race Serilog's own sink open).
/// The ProgramData default is assumed writable (the LocalSystem service account can
/// always write under <c>%ProgramData%</c>), so it is returned unconditionally on
/// fallback without a second probe.
/// </summary>
public sealed class LogPathResolver : ILogPathResolver
{
    private readonly IFileSystem _fs;

    public LogPathResolver(IFileSystem fs)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
    }

    public LogPathResolution Resolve(string? requestedPath)
    {
        var programDataDefault = BuildProgramDataDefault();

        // No request → use the intended default; this is not a fallback.
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return new LogPathResolution(programDataDefault, FellBack: false, Reason: null);
        }

        // Parent directory must exist before we can write the file there.
        var directory = _fs.Path.GetDirectoryName(requestedPath);
        if (!string.IsNullOrEmpty(directory) && !_fs.Directory.Exists(directory))
        {
            return new LogPathResolution(
                programDataDefault,
                FellBack: true,
                Reason: $"Log directory not found: {directory}");
        }

        // Probe writability by opening the file for append and closing it.
        try
        {
            using var stream = _fs.File.Open(requestedPath, FileMode.Append, FileAccess.Write);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or IOException
                                       or DirectoryNotFoundException)
        {
            return new LogPathResolution(
                programDataDefault,
                FellBack: true,
                Reason: $"Log file not writable ({requestedPath}): {ex.Message}");
        }

        return new LogPathResolution(requestedPath, FellBack: false, Reason: null);
    }

    private string BuildProgramDataDefault()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return _fs.Path.Combine(programData, "FreeBird", "logs", "freebird-service-.log");
    }
}
