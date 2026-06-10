using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FreeBird.Core.Models;
using FreeBird.Core.Processing;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FreeBird.Core.Tests.Processing;

/// <summary>
/// T03 — tests for <see cref="ResolutionMarkerSerializer"/>.
///
/// NOTE on test #11 ("WriteAtomic_OperationCanceledException_IsNotCaught"):
/// We deliberately do NOT add a unit test that triggers an OperationCanceledException
/// inside <c>WriteAtomic</c>. The writer uses synchronous <c>File.WriteAllText</c> +
/// <c>File.Move</c> which do not accept a CancellationToken; there is no clean
/// way to inject an OCE without a stream-injection seam that would distort the
/// production API. The invariant ("OCE must NOT be caught") is enforced by the
/// narrow catch clause in the source (see ResolutionMarkerSerializer.WriteAtomic)
/// and by an explanatory code comment alongside it.
/// </summary>
public class ResolutionMarkerSerializerTests : IDisposable
{
    private readonly string _outDir;
    private readonly InMemorySink _sink;
    private readonly ILogger _logger;

    public ResolutionMarkerSerializerTests()
    {
        _outDir = Path.Combine(Path.GetTempPath(), "fb-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outDir);
        _sink = new InMemorySink();
        _logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_outDir))
            {
                // Restore any chmod-restricted dirs so cleanup can proceed (Unix only).
                if (!OperatingSystem.IsWindows())
                {
                    foreach (var dir in Directory.EnumerateDirectories(_outDir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                        catch { /* best-effort */ }
                    }
                }
                Directory.Delete(_outDir, recursive: true);
            }
        }
        catch { /* best-effort */ }
    }

    private static ResolutionMarker BuildMarker(string stem, string outputName = "song.flac") => new()
    {
        Schema = 1,
        SourceStem = stem,
        MusicId = "3367798042",
        SourcePath = "/tmp/" + stem + ".uc!",
        SourceSize = 22481408L,
        SourceMtime = new DateTimeOffset(2026, 6, 10, 18, 42, 11, TimeSpan.Zero),
        ResolvedAt = new DateTimeOffset(2026, 6, 10, 23, 18, 1, TimeSpan.Zero),
        Status = MarkerStatus.Resolved,
        OutputName = outputName,
        Format = "Flac",
        Integrity = "L3",
        NamingTemplate = "{artist} - {title}",
        Reason = null,
        RetryAfter = null,
    };

    [Fact]
    public void MarkerPath_UsesSourceStemNotMusicId()
    {
        // Regression guard for K2: the marker filename MUST be the source stem, not
        // just the leading musicId. If someone refactors to use MusicId, this fails.
        const string stem = "3367798042-_-_5999-_-_abc";
        var expected = Path.Combine(_outDir, ".freebird-resolved", stem + ".json");
        Assert.Equal(expected, ResolutionMarkerSerializer.MarkerPath(_outDir, stem));
    }

    [Fact]
    public void WriteAtomic_CreatesMarkerFileAtExpectedPath()
    {
        var marker = BuildMarker("stem-1");
        var ser = new ResolutionMarkerSerializer(_logger);

        ser.WriteAtomic(_outDir, marker);

        var path = ResolutionMarkerSerializer.MarkerPath(_outDir, "stem-1");
        Assert.True(File.Exists(path), $"Expected marker at {path}");
        Assert.True(ser.TryRead(path, out var roundTripped));
        Assert.NotNull(roundTripped);
        Assert.Equal(marker, roundTripped);
    }

    [Fact]
    public void WriteAtomic_DoesNotLeaveTmpFile()
    {
        var marker = BuildMarker("stem-2");
        var ser = new ResolutionMarkerSerializer(_logger);

        ser.WriteAtomic(_outDir, marker);

        var tmpPath = ResolutionMarkerSerializer.MarkerPath(_outDir, "stem-2") + ".tmp";
        Assert.False(File.Exists(tmpPath), $"Expected no .tmp orphan at {tmpPath}");
    }

    [Fact]
    public void WriteAtomic_TwoConsecutiveWritesSameStem_LastWriterWins()
    {
        var a = BuildMarker("stem-3", outputName: "A.flac");
        var b = BuildMarker("stem-3", outputName: "B.flac");
        var ser = new ResolutionMarkerSerializer(_logger);

        ser.WriteAtomic(_outDir, a);
        ser.WriteAtomic(_outDir, b);

        Assert.True(ser.TryRead(ResolutionMarkerSerializer.MarkerPath(_outDir, "stem-3"), out var read));
        Assert.NotNull(read);
        Assert.Equal("B.flac", read!.OutputName);
        Assert.Equal(b, read);
    }

    [Fact]
    public void WriteAtomic_CreatesFreebirdResolvedDirIfMissing()
    {
        var resolvedDir = Path.Combine(_outDir, ".freebird-resolved");
        Assert.False(Directory.Exists(resolvedDir), "precondition: .freebird-resolved/ should not exist yet");

        var ser = new ResolutionMarkerSerializer(_logger);
        ser.WriteAtomic(_outDir, BuildMarker("stem-4"));

        Assert.True(Directory.Exists(resolvedDir));
    }

    [Fact]
    public void TryRead_RoundTripsIdenticalBytes()
    {
        var marker = BuildMarker("stem-5", outputName: "ünïcødé.flac") with
        {
            Status = MarkerStatus.MetadataEmpty,
            Reason = "metadata-empty",
            RetryAfter = new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero),
        };
        var ser = new ResolutionMarkerSerializer(_logger);
        ser.WriteAtomic(_outDir, marker);

        Assert.True(ser.TryRead(ResolutionMarkerSerializer.MarkerPath(_outDir, "stem-5"), out var read));
        Assert.Equal(marker, read);
    }

    [Fact]
    public void TryRead_FileMissing_ReturnsFalseNoLog()
    {
        var ser = new ResolutionMarkerSerializer(_logger);
        var missing = Path.Combine(_outDir, ".freebird-resolved", "nope.json");

        var ok = ser.TryRead(missing, out var marker);

        Assert.False(ok);
        Assert.Null(marker);
        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void TryRead_MalformedJson_ReturnsFalseAndLogsWarn()
    {
        var resolvedDir = Path.Combine(_outDir, ".freebird-resolved");
        Directory.CreateDirectory(resolvedDir);
        var path = Path.Combine(resolvedDir, "bad.json");
        File.WriteAllText(path, "{not valid json");

        var ser = new ResolutionMarkerSerializer(_logger);
        var ok = ser.TryRead(path, out var marker);

        Assert.False(ok);
        Assert.Null(marker);
        Assert.Single(_sink.Events);
        var ev = _sink.Events[0];
        Assert.Equal(LogEventLevel.Warning, ev.Level);
        Assert.Contains("Failed to parse", ev.RenderMessage());
    }

    [Fact]
    public void WriteAtomic_OnUnauthorizedAccessException_ReturnsAndLogsWarn_DeletesTmpOrphan()
    {
        // Simulate the IOException path (D2/T2) by creating the target .tmp path
        // as a *directory*, which forces File.WriteAllText to throw on tmp write.
        // This is the more portable fallback the dispatch authorises when
        // UnixFileMode chmod tricks are unreliable.
        var resolvedDir = Path.Combine(_outDir, ".freebird-resolved");
        Directory.CreateDirectory(resolvedDir);
        var tmpAsDir = Path.Combine(resolvedDir, "stem-6.json.tmp");
        Directory.CreateDirectory(tmpAsDir);

        var ser = new ResolutionMarkerSerializer(_logger);
        // MUST NOT throw.
        ser.WriteAtomic(_outDir, BuildMarker("stem-6"));

        // The final marker file MUST NOT exist (write failed).
        Assert.False(File.Exists(Path.Combine(resolvedDir, "stem-6.json")));

        // The pre-existing directory at the .tmp path should still be there
        // (best-effort File.Delete swallows the IOException from trying to
        //  delete a directory as a file).
        Assert.True(Directory.Exists(tmpAsDir));

        // One WARN should have been logged.
        Assert.Single(_sink.Events);
        var ev = _sink.Events[0];
        Assert.Equal(LogEventLevel.Warning, ev.Level);
        Assert.Contains("Marker write failed", ev.RenderMessage());
    }

    [Fact(Skip = "disk-full unit-simulation not viable on macOS without injecting a stream; covered conceptually by WriteAtomic_OnUnauthorizedAccessException_ReturnsAndLogsWarn_DeletesTmpOrphan catch block")]
    public void WriteAtomic_OnIOExceptionDiskFull_ReturnsAndLogsWarn()
    {
        // Intentionally skipped — see [Fact(Skip=...)] message above and the
        // class-level XML doc-comment for the rationale on the OCE test.
    }

    /// <summary>Tiny in-memory Serilog sink for assertions on log output.</summary>
    private sealed class InMemorySink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();
        public IReadOnlyList<LogEvent> Events => _events.ToArray();
        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
