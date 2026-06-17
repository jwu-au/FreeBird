using System;
using System.IO;
using System.IO.Abstractions;
using FluentAssertions;
using FreeBird.Cli.Service;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T24 — Windows-only integration tests for the design §2.4 log-file fallback flow,
/// exercised through the REAL <see cref="LogPathResolver"/> against a REAL
/// <see cref="FileSystem"/>.
///
/// <para><b>Scope honesty.</b> <see cref="LogPathResolver"/> is OS-agnostic and pure —
/// it RESOLVES the final Serilog file-sink path and SIGNALS a fallback via
/// <see cref="LogPathResolution.FellBack"/> + <see cref="LogPathResolution.Reason"/>.
/// It deliberately does NOT write to the Event Log. The actual §2.4 Event-Log WARNING
/// on fallback is emitted by the Windows-only host body
/// <c>RunCommand.Windows.cs</c> (lines 62-67), which routes
/// <c>serilogLogger.Warning(...)</c> to the EventLog Serilog sink configured at
/// <c>RunCommand.Windows.cs:59</c>. That host body requires a full Windows Service
/// host + <c>WatchSupervisor</c> to boot, so the WARNING <i>emission</i> is verified by
/// Windows CI (manual/integration), not reproduced here. These tests assert the
/// resolution + fallback signal that drives that WARNING.</para>
///
/// This file is kept in the Windows-only compile set (see
/// <c>FreeBird.Cli.Tests.csproj</c>) per the T24 scope (Windows integration). On macOS/Linux
/// it is compile-excluded; on Windows CI it compiles and runs. The per-test
/// <c>Skip.IfNot(OperatingSystem.IsWindows())</c> is belt-and-suspenders.
/// </summary>
[Trait("os", "windows")]
public sealed class LogFileFallbackIntegrationTests
{
    [SkippableFact]
    public void Resolve_MissingDirectory_FallsBackToProgramDataAndSignals()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Log-file fallback integration is exercised on Windows CI");

        // A path whose parent directory does not exist must trigger the fallback.
        var missingDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "watch.log");
        var sut = new LogPathResolver(new FileSystem());

        var resolution = sut.Resolve(missingDir);

        resolution.FellBack.Should().BeTrue("a missing parent directory is unwritable");
        resolution.Reason.Should().NotBeNullOrWhiteSpace("the fallback reason must explain the failure");
        resolution.FinalPath.Should().Contain(
            Path.Combine("FreeBird", "logs"),
            "the fallback must be the ProgramData default log path");
    }

    [SkippableFact]
    public void Resolve_WritablePath_UsesRequestedPathAndDoesNotFallBack()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Log-file fallback integration is exercised on Windows CI");

        // A real, writable temp directory must be used verbatim with no fallback.
        var writableRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(writableRoot);
        var requested = Path.Combine(writableRoot, "watch.log");
        var sut = new LogPathResolver(new FileSystem());

        try
        {
            var resolution = sut.Resolve(requested);

            resolution.FellBack.Should().BeFalse("a writable requested path must be used as-is");
            resolution.FinalPath.Should().Be(requested, "the resolver must return the requested path unchanged");
            resolution.Reason.Should().BeNull("no fallback means no reason");
        }
        finally
        {
            try
            {
                Directory.Delete(writableRoot, recursive: true);
            }
            catch
            {
                // Swallow — cleanup must never fail a test run.
            }
        }
    }

    [SkippableFact]
    public void Resolve_NullRequest_UsesProgramDataDefaultWithoutFallback()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Log-file fallback integration is exercised on Windows CI");

        var sut = new LogPathResolver(new FileSystem());

        var resolution = sut.Resolve(null);

        resolution.FellBack.Should().BeFalse("the default is the intended behaviour, not a fallback");
        resolution.Reason.Should().BeNull("an intended default carries no fallback reason");
        resolution.FinalPath.Should().Contain(
            Path.Combine("FreeBird", "logs"),
            "a null request must resolve to the ProgramData default");
    }
}
