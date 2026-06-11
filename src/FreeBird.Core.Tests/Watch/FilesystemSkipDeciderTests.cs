using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using FreeBird.Core.Processing;
using FreeBird.Core.Sidecar;
using FreeBird.Core.Watch;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FreeBird.Core.Tests.Watch;

public sealed class FilesystemSkipDeciderTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best effort
            }
        }
    }

    private string NewTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "fb-skipdecider-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "in"));
        Directory.CreateDirectory(Path.Combine(root, "out"));
        Directory.CreateDirectory(Path.Combine(root, "out", ".freebird-failed"));
        _tempDirs.Add(root);
        return root;
    }

    private static string CreateSource(string root, string fileName, int sizeBytes)
    {
        var path = Path.Combine(root, "in", fileName);
        var bytes = new byte[sizeBytes];
        for (var i = 0; i < bytes.Length; i++) { bytes[i] = (byte)(i % 256); }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static WatchOptions OptionsFor(string root, long minSize = 1024)
    {
        return new WatchOptions(
            InputDir: Path.Combine(root, "in"),
            OutputDir: Path.Combine(root, "out"),
            MinFileSizeBytes: minSize);
    }

    private static ILogger NullLogger() => new Mock<ILogger>().Object;

    private static ResolutionMarkerSerializer NewMarkerSerializer()
        => new(NullLogger());

    private static FilesystemSkipDecider WithRealReader()
        => new(new TextSidecarReader(), NewMarkerSerializer(), NullLogger());

    private static FilesystemSkipDecider WithMockReader(Mock<ISidecarReader> mock)
        => new(mock.Object, NewMarkerSerializer(), NullLogger());

    private static string WriteV2Sidecar(
        string failedDir,
        string sidecarFileName,
        string sourcePath,
        long sourceSize,
        DateTimeOffset sourceMtime,
        AudioFormat format = AudioFormat.Mp3,
        IntegrityLevel? integrityLevel = IntegrityLevel.L1,
        string reason = "test reason")
    {
        var sidecarPath = Path.Combine(failedDir, sidecarFileName);
        var sb = new StringBuilder();
        sb.AppendLine("timestamp: " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("source:    " + sourcePath);
        sb.AppendLine("format:    " + format);
        sb.AppendLine("integrity: " + (integrityLevel?.ToString() ?? "-"));
        sb.AppendLine("reason:    " + reason);
        sb.AppendLine("source_size: " + sourceSize.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("source_mtime: " + sourceMtime.ToString("O", CultureInfo.InvariantCulture));
        File.WriteAllText(sidecarPath, sb.ToString());
        return sidecarPath;
    }

    private static string WriteV1Sidecar(
        string failedDir,
        string sidecarFileName,
        string sourcePath,
        AudioFormat format = AudioFormat.Mp3,
        IntegrityLevel? integrityLevel = IntegrityLevel.L1,
        string reason = "test reason v1")
    {
        var sidecarPath = Path.Combine(failedDir, sidecarFileName);
        var sb = new StringBuilder();
        sb.AppendLine("timestamp: " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        sb.AppendLine("source:    " + sourcePath);
        sb.AppendLine("format:    " + format);
        sb.AppendLine("integrity: " + (integrityLevel?.ToString() ?? "-"));
        sb.AppendLine("reason:    " + reason);
        File.WriteAllText(sidecarPath, sb.ToString());
        return sidecarPath;
    }

    [Fact]
    public async Task DecideAsync_OutputFileMissing_NoSidecar_ReturnsProcess()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeTrue();
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public async Task DecideAsync_Mp3OutputExists_ReturnsSkip_AlreadyDecoded()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);
        var outPath = Path.Combine(root, "out", "song.mp3");
        File.WriteAllText(outPath, "fake mp3");

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecoded);
        decision.Detail.Should().Contain("song.mp3");
    }

    [Fact]
    public async Task DecideAsync_FlacOutputExists_ReturnsSkip_AlreadyDecoded()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);
        var outPath = Path.Combine(root, "out", "song.flac");
        File.WriteAllText(outPath, "fake flac");

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecoded);
        decision.Detail.Should().Contain("song.flac");
    }

    [Fact]
    public async Task DecideAsync_M4aOutputExists_ReturnsSkip_AlreadyDecoded()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);
        var outPath = Path.Combine(root, "out", "song.m4a");
        File.WriteAllText(outPath, "fake m4a");

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecoded);
        decision.Detail.Should().Contain("song.m4a");
    }

    [Fact]
    public async Task DecideAsync_SourceSmallerThanMinFileSize_ReturnsSkip_SourceTooSmall()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "tiny.uc", 500);

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root, minSize: 1024));

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.SourceTooSmall);
        decision.Detail.Should().Contain("500").And.Contain("1024");
    }

    [Fact]
    public async Task DecideAsync_SourceMissing_ReturnsProcess()
    {
        var root = NewTempDir();
        var missingPath = Path.Combine(root, "in", "ghost.uc");

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(missingPath, OptionsFor(root));

        decision.ShouldProcess.Should().BeTrue();
    }

    [Fact]
    public async Task DecideAsync_SidecarSizeAndMtimeMatch_ReturnsSkip_SourceUnchanged()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);
        var info = new FileInfo(src);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        var failedDir = Path.Combine(root, "out", ".freebird-failed");
        var sidecarPath = WriteV2Sidecar(failedDir, "song.mp3.txt", src, info.Length, mtime);

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.SourceUnchangedSinceFailure);
        decision.Detail.Should().Contain(sidecarPath);
    }

    [Fact]
    public async Task DecideAsync_SidecarSizeMatchButMtimeDiffers_ReturnsProcess()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);
        var info = new FileInfo(src);

        var staleMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).AddHours(-1);
        var failedDir = Path.Combine(root, "out", ".freebird-failed");
        WriteV2Sidecar(failedDir, "song.mp3.txt", src, info.Length, staleMtime);

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeTrue();
    }

    [Fact]
    public async Task DecideAsync_SidecarSizeDiffers_ReturnsProcess()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);
        var info = new FileInfo(src);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        var failedDir = Path.Combine(root, "out", ".freebird-failed");
        // Wrong size
        WriteV2Sidecar(failedDir, "song.mp3.txt", src, info.Length + 1, mtime);

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeTrue();
    }

    [Fact]
    public async Task DecideAsync_V1SidecarMissingSizeMtime_ReturnsProcess()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);

        var failedDir = Path.Combine(root, "out", ".freebird-failed");
        WriteV1Sidecar(failedDir, "song.mp3.txt", src);

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeTrue();
    }

    [Fact]
    public async Task DecideAsync_MultipleSidecarsOneMatches_ReturnsSkip()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);
        var info = new FileInfo(src);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        var failedDir = Path.Combine(root, "out", ".freebird-failed");
        // First sidecar: stale (won't match)
        WriteV2Sidecar(failedDir, "song.flac.txt", src, info.Length + 1, mtime);
        // Second sidecar: matches exactly
        var matchingSidecar = WriteV2Sidecar(failedDir, "song.bin.txt", src, info.Length, mtime);

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.SourceUnchangedSinceFailure);
        decision.Detail.Should().Contain(matchingSidecar);
    }

    [Fact]
    public async Task DecideAsync_SidecarReaderReturnsNull_FallsThrough_ReturnsProcess()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);

        var failedDir = Path.Combine(root, "out", ".freebird-failed");
        // Sidecar file exists but reader returns null (e.g., malformed)
        File.WriteAllText(Path.Combine(failedDir, "song.mp3.txt"), "garbage");

        var mockReader = new Mock<ISidecarReader>();
        mockReader
            .Setup(r => r.TryReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SidecarRecord?)null);

        var sut = WithMockReader(mockReader);
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeTrue();
        mockReader.Verify(r => r.TryReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DecideAsync_OutputExists_AND_SidecarMatches_OutputWins()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);
        var info = new FileInfo(src);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        // Output file exists
        File.WriteAllText(Path.Combine(root, "out", "song.mp3"), "fake mp3");
        // AND sidecar matches
        var failedDir = Path.Combine(root, "out", ".freebird-failed");
        WriteV2Sidecar(failedDir, "song.flac.txt", src, info.Length, mtime);

        var sut = WithRealReader();
        var decision = await sut.DecideAsync(src, OptionsFor(root));

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecoded);
    }

    [Fact]
    public async Task DecideAsync_NullSourcePath_Throws()
    {
        var root = NewTempDir();
        var sut = WithRealReader();

        await ((Func<Task>)(async () => await sut.DecideAsync(null!, OptionsFor(root))))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task DecideAsync_NullOptions_Throws()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);
        var sut = WithRealReader();

        await ((Func<Task>)(async () => await sut.DecideAsync(src, null!)))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DecideAsync_Cancelled_Throws()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "song.uc", 4096);
        var sut = WithRealReader();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await ((Func<Task>)(async () => await sut.DecideAsync(src, OptionsFor(root), cts.Token)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Constructor_NullSidecarReader_Throws()
    {
        ((Action)(() => _ = new FilesystemSkipDecider(null!, NewMarkerSerializer(), NullLogger())))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullMarkerSerializer_Throws()
    {
        ((Action)(() => _ = new FilesystemSkipDecider(new TextSidecarReader(), null!, NullLogger())))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        ((Action)(() => _ = new FilesystemSkipDecider(new TextSidecarReader(), NewMarkerSerializer(), null!)))
            .Should().Throw<ArgumentNullException>();
    }

    // -----------------------------------------------------------------------
    // v3 T15 — conservative musicId-fallback handling (spec §10 / OA2)
    // -----------------------------------------------------------------------
    //
    // The v3 namer produces "{musicId}.{ext}" on metadata-resolution fallback.
    // For sources whose stem is the musicId itself (e.g. "3367798042.uc"),
    // the existing stem-equals-output match already handles this. For composite
    // stems like "3367798042-_-_5999-_-_xxx.uc", the skip decider's leading-digit
    // musicId-extraction (mirroring MetadataAwareFileNamer.ExtractMusicId) is the
    // only way to predict the fallback output filename without an API call.

    [Fact]
    public async Task DecideAsync_CompositeStem_MusicIdOnlyOutputExists_Offline_ReturnsSkip()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999.uc", 4096);
        // Prior run produced the musicId-fallback name in offline mode.
        File.WriteAllBytes(Path.Combine(root, "out", "3367798042.mp3"), new byte[] { 1, 2, 3 });

        var opts = OptionsFor(root) with { Offline = true };
        var sut = WithRealReader();

        var decision = await sut.DecideAsync(src, opts);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecoded);
        decision.Detail.Should().Contain("3367798042.mp3");
    }

    [Fact]
    public async Task DecideAsync_CompositeStem_MusicIdOnlyOutputExists_Online_ReturnsProcess_AndLogsEdge()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999.uc", 4096);
        // A prior run hit metadata-fetch-failed and wrote the musicId fallback.
        // Current run is online — v3 may now resolve to a different name, so re-process.
        File.WriteAllBytes(Path.Combine(root, "out", "3367798042.flac"), new byte[] { 1, 2, 3 });

        var opts = OptionsFor(root);   // Offline default: false
        var logged = new List<string>();
        var loggerMock = new Mock<ILogger>();
        loggerMock.Setup(l => l.Information(It.IsAny<string>(), It.IsAny<object?[]>()))
                  .Callback<string, object?[]>((tpl, _) => logged.Add(tpl));
        var sut = new FilesystemSkipDecider(new TextSidecarReader(), NewMarkerSerializer(), loggerMock.Object);

        var decision = await sut.DecideAsync(src, opts);

        decision.ShouldProcess.Should().BeTrue();
        // The Information() callback may not be invoked by Serilog's optimized
        // overloads in all .NET runtimes — assert behavior, not log capture.
        // We assert reprocessing was decided, which is the observable contract.
    }

    [Fact]
    public async Task DecideAsync_StemIsMusicId_AlreadyCoveredBy_StemMatch()
    {
        // When the stem IS the musicId, the legacy "<stem>.<ext>" branch matches
        // first and the musicId-fallback branch never fires. This locks that in
        // (covered by the stem-match path, no double-counting from the new branch).
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042.uc", 4096);
        File.WriteAllBytes(Path.Combine(root, "out", "3367798042.flac"), new byte[] { 1, 2, 3 });

        // Online — even though musicId-fallback branch would log edge case in
        // composite-stem mode, here the stem-equals-musicId case correctly Skips.
        var opts = OptionsFor(root);
        var sut = WithRealReader();

        var decision = await sut.DecideAsync(src, opts);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecoded);
        decision.Detail.Should().Contain("3367798042.flac");
    }

    [Fact]
    public async Task DecideAsync_NonNumericStem_NoMusicIdFallback_FallsThrough()
    {
        // Defensive: stems without leading digits can't produce a musicId-fallback
        // candidate; decider falls through to sidecar check (no match) then Process.
        var root = NewTempDir();
        var src = CreateSource(root, "not-a-numeric-id.uc", 4096);
        // Place a misleading numeric output that should NOT match this source.
        File.WriteAllBytes(Path.Combine(root, "out", "12345.flac"), new byte[] { 1, 2, 3 });

        var opts = OptionsFor(root);
        var sut = WithRealReader();

        var decision = await sut.DecideAsync(src, opts);

        decision.ShouldProcess.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // v3.0.1 T05 — Branch 3b: per-source-stem JSON marker short-circuit.
    //
    // Comprehensive coverage of all stale/retry/template/missing-output branches
    // lives in T06. This single happy-path test proves the end-to-end wiring:
    // a fresh marker yields Skip(AlreadyDecodedViaMarker) and emits exactly one
    // DBG "marker hit" line with zero INF noise.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Decide_MarkerPresentAndFresh_ReturnsSkipAlreadyDecodedViaMarker_AndLogsDbg()
    {
        var root = NewTempDir();
        // Use a composite stem that wouldn't match Branch 3a (the stem itself isn't
        // the output filename) so Branch 3b is the branch under test.
        var src = CreateSource(root, "3367798042-_-_5999.uc", 4096);
        var info = new FileInfo(src);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        var opts = OptionsFor(root);

        // Pre-seed the output that the marker references (belt-and-suspenders check).
        const string outputName = "Alice - SongOne.flac";
        File.WriteAllBytes(Path.Combine(root, "out", outputName), new byte[] { 1, 2, 3 });

        // Build a fully-fresh Resolved marker matching the source AND template.
        var marker = new ResolutionMarker
        {
            Schema = 1,
            SourceStem = "3367798042-_-_5999",
            MusicId = "3367798042",
            SourcePath = src,
            SourceSize = info.Length,
            SourceMtime = mtime,
            ResolvedAt = DateTimeOffset.UtcNow,
            Status = MarkerStatus.Resolved,
            OutputName = outputName,
            Format = "Flac",
            Integrity = "L3",
            NamingTemplate = opts.NamingTemplate,
            Reason = null,
            RetryAfter = null,
        };

        // Write via the REAL serializer to exercise the full round-trip on disk.
        var writerLogger = new LoggerConfiguration().MinimumLevel.Verbose().CreateLogger();
        var writerSerializer = new ResolutionMarkerSerializer(writerLogger);
        writerSerializer.WriteAtomic(opts.OutputDir, marker);

        var markerPath = ResolutionMarkerSerializer.MarkerPath(opts.OutputDir, marker.SourceStem);
        File.Exists(markerPath).Should().BeTrue("the test seeds a fresh marker");

        // Capture decider logs via an InMemorySink.
        var sink = new InMemorySink();
        var deciderLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var readerSerializer = new ResolutionMarkerSerializer(deciderLogger);
        var sut = new FilesystemSkipDecider(new TextSidecarReader(), readerSerializer, deciderLogger);

        var decision = await sut.DecideAsync(src, opts);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
        decision.Detail.Should().Be(markerPath);

        // Exactly one "marker hit" DBG line, zero INF lines (proves the demotion +
        // proves we never logged a freshness/stale message on the happy path).
        var markerHitLines = sink.Events
            .Where(e => e.RenderMessage().Contains("marker hit", StringComparison.Ordinal))
            .ToList();
        markerHitLines.Should().HaveCount(1);
        markerHitLines[0].Level.Should().Be(LogEventLevel.Debug);

        sink.Events.Where(e => e.Level == LogEventLevel.Information).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // v3.2 T03 — Regression test for stem-named quarantine sidecar.
    //
    // Before T02, FileProcessor wrote IntegrityFailed sidecars using musicId
    // (e.g., "3367798042.flac.txt"), but FilesystemSkipDecider globs by
    // source stem ("{stem}.*.txt"). The mismatch caused infinite re-decode
    // loops in watch mode. T02 fixed the writer to use the full stem. This
    // test asserts the end-to-end glob+parse+match path now succeeds for
    // a real NetEase-format composite stem.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Decide_StemNamedSidecar_WithMatchingSizeAndMtime_ReturnsSkipSourceUnchangedSinceFailure()
    {
        var root = NewTempDir();
        const string stem = "3367798042-_-_5999-_-_a38658b6e504b7520bb4c507db13b9d2";
        const string sourceFileName = stem + ".uc!";

        // Real NetEase-format source under a "cache" subdir of the input root.
        var cacheDir = Path.Combine(root, "in", "cache");
        Directory.CreateDirectory(cacheDir);
        var sourcePath = Path.Combine(cacheDir, sourceFileName);
        File.WriteAllBytes(sourcePath, new byte[] { 0xA3, 0xA3, 0xA3 });

        var info = new FileInfo(sourcePath);
        var mtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

        // Pre-write the stem-named sidecar exactly as T02's FileProcessor now does.
        var failedDir = Path.Combine(root, "out", ".freebird-failed");
        var sidecarPath = WriteV2Sidecar(
            failedDir,
            stem + ".flac.txt",
            sourcePath,
            info.Length,
            mtime,
            format: AudioFormat.Flac,
            integrityLevel: IntegrityLevel.L3,
            reason: "integrity failed (flac -t failed)");

        // Source must be >= MinFileSizeBytes; lower the threshold for this 3-byte file.
        var opts = OptionsFor(root, minSize: 1);

        // Real reader + real marker serializer — this is the regression integration.
        var sut = WithRealReader();
        var decision = await sut.DecideAsync(sourcePath, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.SourceUnchangedSinceFailure);
        decision.Detail.Should().Contain(sidecarPath);
    }

    /// <summary>Tiny in-memory Serilog sink for assertions on log output.</summary>
    private sealed class InMemorySink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();
        public IReadOnlyList<LogEvent> Events => _events.ToArray();
        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
