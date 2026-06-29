using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Infrastructure;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using FreeBird.Core.Processing;
using FreeBird.Core.Sidecar;
using FreeBird.Core.Tests.Ncm;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;
using Xunit;

namespace FreeBird.Core.Tests.Watch;

/// <summary>
/// Task 19 — watch integration: a <c>.ncm</c> dropped into a watched dir is decoded
/// exactly ONCE; subsequent cycles SKIP it via the resolution marker. This is the
/// watch analogue of Task 16, but driven through the REAL watch path:
/// <see cref="WatchOrchestrator"/> → real <see cref="SizeStabilityCompletionDetector"/>
/// (completion) → real <see cref="FilesystemSkipDecider"/> → real
/// <see cref="ResolvedMarkerGate"/> (Branch 3b) → router → processor.
///
/// The ncm processor is wrapped in a call-COUNTING spy so we can assert it ran once
/// and not again. Determinism: <c>FakeTimeProvider</c> drives the polling loop (no real
/// timers/sleeps) and <c>StabilityChecks=1</c> so a complete .ncm stabilises in a
/// single observation per cycle.
/// </summary>
public sealed class NcmWatchIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-ncm-watch-{Guid.NewGuid():N}");
    private readonly string _inputDir;
    private readonly string _outputDir;
    private readonly Serilog.Core.Logger _silentLogger;

    public NcmWatchIntegrationTests()
    {
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
        _silentLogger = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        _silentLogger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Poll <paramref name="condition"/> until it returns true or the timeout elapses.
    /// Avoids a fixed-delay race: the watch cycle runs fire-and-forget, so we wait for
    /// the observable effect (output file present, spy call count) rather than guessing.
    /// </summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>
    /// A thin <see cref="IFileProcessor"/> spy that counts ProcessAsync calls and
    /// delegates to a real inner processor.
    /// </summary>
    private sealed class CountingProcessor : IFileProcessor
    {
        private readonly IFileProcessor _inner;
        public int Calls;

        public CountingProcessor(IFileProcessor inner) => _inner = inner;

        public Task<ScanResult> ProcessAsync(string sourcePath, ScanOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return _inner.ProcessAsync(sourcePath, options, cancellationToken);
        }
    }

    /// <summary>Routes <c>.ncm</c> to the spy; anything else to a never-used stub.</summary>
    private sealed class NcmOnlyRouter : IFileProcessorRouter
    {
        private readonly IFileProcessor _ncm;
        public NcmOnlyRouter(IFileProcessor ncm) => _ncm = ncm;
        public IFileProcessor Select(string sourcePath) => _ncm;
    }

    private static NcmFixtureMeta FlacMeta() =>
        new(MusicId: 333, MusicName: "Watch Song", Album: "Watch Album",
            Artists: new[] { ("Watch Artist", 3L) }, Format: "flac");

    [Fact]
    public async Task Watch_DecodesNcmOnce_ThenSkipsOnSubsequentCycle()
    {
        // --- Arrange: a complete flac-bodied .ncm with an embedded cover. ---
        var flacBody = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleFlacPath);
        byte[] cover = { 0xFF, 0xD8, 0xFF, 0xE0, 0x11, 0x22, 0x33 }; // JPEG-ish
        var ncmBytes = NcmTestEncoder.Encode(flacBody, FlacMeta(), cover);
        var ncmPath = Path.Combine(_inputDir, "song.ncm");
        await File.WriteAllBytesAsync(ncmPath, ncmBytes);

        var logger = (ILogger)_silentLogger;
        var clock = new FakeTimeProvider();

        var integrity = new Mock<ICompositeIntegrityChecker>();
        integrity
            .Setup(i => i.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.Off));

        var tagWriter = new Mock<ITagWriter>();
        tagWriter
            .Setup(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);

        // Cover-write spy: confirms the watch path exercises the embedded-cover branch.
        var coverWriter = new Mock<ICoverWriter>();
        coverWriter
            .Setup(c => c.WriteCoverAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);

        var markerSerializer = new ResolutionMarkerSerializer(logger);

        var realNcmProcessor = new NcmFileProcessor(
            new NcmDecoder(),
            new MagicByteFormatSniffer(),
            integrity.Object,
            new AtomicFileWriter(),
            tagWriter.Object,
            coverWriter.Object,
            markerSerializer,
            new OutputPathMutexPool(),
            logger,
            clock);

        var spy = new CountingProcessor(realNcmProcessor);
        var router = new NcmOnlyRouter(spy);

        // Real watch collaborators: completion detector, marker gate, skip decider.
        var detector = new SizeStabilityCompletionDetector();
        var gate = new ResolvedMarkerGate(markerSerializer, logger, clock);
        var skipDecider = new FilesystemSkipDecider(new TextSidecarReader(), logger, gate);

        // Real scan orchestrator for the initial sweep (also marker-gated, same router).
        var scanOrchestrator = new ScanOrchestrator(router, logger, gate);

        var orchestrator = new WatchOrchestrator(
            scanOrchestrator,
            detector,
            skipDecider,
            router,
            clock,
            logger);

        // StabilityChecks=1 → a complete .ncm is "stable" on first observation each cycle.
        // SkipInitialScan=true → the polling loop (not the sweep) does the first decode,
        // so we can count cycles precisely. MinFileSizeBytes small so the .ncm passes.
        var options = new WatchOptions(
            InputDirs: new[] { _inputDir },
            OutputDir: _outputDir,
            Integrity: IntegrityLevel.Off,
            Concurrency: 1,
            Collision: CollisionPolicy.Skip,
            PollInterval: TimeSpan.FromSeconds(5),
            StabilityChecks: 1,
            MinFileSizeBytes: 64,
            SkipInitialScan: true);

        using var soft = new CancellationTokenSource();
        var runTask = orchestrator.RunAsync(options, soft.Token, CancellationToken.None);

        // --- Cycle 1: decode the .ncm. Wait for the observable effect — specifically
        // the RESOLUTION MARKER, which is the LAST artifact ProcessAsync writes (after
        // the atomic move + tag/cover). Waiting on the marker (not the output file)
        // guarantees the whole cycle completed, avoiding a mid-process race. The cycle
        // runs fire-and-forget, so we poll rather than use a fixed delay. ---
        var expectedOutput = Path.Combine(_outputDir, "Watch Artist - Watch Song.flac");
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "song");
        clock.Advance(TimeSpan.FromSeconds(5));
        var done = await WaitForAsync(() => File.Exists(markerPath), TimeSpan.FromSeconds(10));
        done.Should().BeTrue("the first cycle must decode the .ncm and write its Resolved marker");
        spy.Calls.Should().Be(1, "the .ncm processor must run exactly once on the first cycle");
        File.Exists(expectedOutput).Should().BeTrue("the first cycle must produce the decoded output");
        var firstWriteUtc = File.GetLastWriteTimeUtc(expectedOutput);

        // --- Deterministic skip proof: the REAL FilesystemSkipDecider (the watch
        // skip path) now returns Skip for this source via the marker (Branch 3b).
        // Stem is "song" but the output is "Watch Artist - Watch Song.flac", so the
        // stem-equals-output branch (3a) cannot match — only the marker gate can. ---
        var decision = await skipDecider.DecideAsync(ncmPath, options);
        decision.ShouldProcess.Should().BeFalse("the resolution marker must drive a watch-cycle skip");
        decision.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);

        // --- Cycle 2 end-to-end: advance the clock to trigger another cycle; give it a
        // bounded settle window; assert the spy was NOT invoked again (no reprocessing)
        // and the output is byte-stable. ---
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitForAsync(() => false, TimeSpan.FromMilliseconds(300));

        spy.Calls.Should().Be(1, "the marker gate must skip the .ncm on the second cycle (no reprocessing)");
        File.GetLastWriteTimeUtc(expectedOutput).Should().Be(firstWriteUtc,
            "the output must be untouched on the second cycle");

        soft.Cancel();
        var summary = await runTask;

        // The watch summary reflects exactly one Ok decode. Note: a skip-decider
        // short-circuit (Branch 3b marker hit) is filtered out BEFORE the processing
        // phase, so it does NOT increment the summary's Skipped counter — the proof of
        // "no reprocessing" is the spy call-count + unchanged output mtime above.
        summary.Ok.Should().Be(1);

        // The cover-write branch was exercised at least once (watch path, embedded cover).
        coverWriter.Verify(
            c => c.WriteCoverAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
}
