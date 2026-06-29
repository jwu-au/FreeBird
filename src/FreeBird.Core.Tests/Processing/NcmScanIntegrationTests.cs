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
using FreeBird.Core.Naming;
using FreeBird.Core.Processing;
using FreeBird.Core.Tests.Ncm;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;
using Xunit;

namespace FreeBird.Core.Tests.Processing;

/// <summary>
/// Task 18 — scan integration over a MIXED input dir holding BOTH a real <c>.uc</c>
/// file and a real <c>.ncm</c> file. Exercises the REAL <see cref="ScanOrchestrator"/>
/// + REAL <see cref="FileProcessorRouter"/> + REAL <see cref="FileProcessor"/> (.uc)
/// + REAL <see cref="NcmFileProcessor"/> (.ncm) + REAL shared collaborators.
///
/// The ONLY mocks are the network metadata resolver (stubbed to an offline Fallback so
/// the .uc path never hits NetEase; mirrors <c>FileProcessorTests</c>' end-to-end test),
/// integrity (faked Ok), and tag/cover writers (no-op spies that would otherwise shell
/// out to metaflac). Proves the router dispatches each extension to the correct
/// processor with NO cross-contamination, and that .uc behavior is unchanged.
/// </summary>
public sealed class NcmScanIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-ncm-scan-{Guid.NewGuid():N}");
    private readonly string _inputDir;
    private readonly string _outputDir;

    public NcmScanIntegrationTests()
    {
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private static NcmFixtureMeta FlacMeta() =>
        new(MusicId: 222, MusicName: "Ncm Title", Album: "Ncm Album",
            Artists: new[] { ("Ncm Artist", 9L) }, Format: "flac");

    [Fact]
    public async Task RunAsync_MixedUcAndNcmFolder_RoutesEachCorrectly_NoCrossContamination()
    {
        // --- Arrange inputs: one real .uc (XOR-0xA3 mp3 fixture) and one real .ncm (flac body). ---
        var ucPath = await Fixtures.Fixtures.WriteFixtureAsUcAsync(
            Fixtures.Fixtures.SampleMp3Path, _inputDir, "12345-mixed.uc");

        var flacBody = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleFlacPath);
        var ncmBytes = NcmTestEncoder.Encode(flacBody, FlacMeta(), cover: null);
        var ncmPath = Path.Combine(_inputDir, "song.ncm");
        await File.WriteAllBytesAsync(ncmPath, ncmBytes);

        var logger = new Mock<ILogger>().Object;

        // Integrity faked to pass for both processors (avoids shelling to flac).
        var integrity = new Mock<ICompositeIntegrityChecker>();
        integrity
            .Setup(i => i.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.Off));

        // The .uc metadata resolver: stubbed to offline Fallback so it NEVER hits the
        // network. The invariant "offline-mode reason ⇔ Offline=true" is honoured by
        // setting Offline=true on the ScanOptions below. A ProcessAsync call into the
        // ncm pipeline would NOT touch this resolver — proving no cross-contamination.
        var metadataResolver = new Mock<IMetadataResolver>();
        metadataResolver
            .Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MetadataResolution.Fallback("offline-mode"));

        // No-op tag/cover spies (would otherwise shell to metaflac). The .ncm path uses
        // the REAL NcmDecoder so it genuinely decodes.
        var tagWriter = new Mock<ITagWriter>();
        tagWriter
            .Setup(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);
        var coverWriter = new Mock<ICoverWriter>();

        // Shared cheap real collaborators.
        var sniffer = new MagicByteFormatSniffer();
        var atomicWriter = new AtomicFileWriter();
        var markerSerializer = new ResolutionMarkerSerializer(logger);
        var mutexPool = new OutputPathMutexPool();
        var timeProvider = new FakeTimeProvider();

        var ucProcessor = new FileProcessor(
            new XorDecoder(),
            sniffer,
            new StemBasedFileNamer(),
            integrity.Object,
            atomicWriter,
            metadataResolver.Object,
            tagWriter.Object,
            markerSerializer,
            mutexPool,
            logger,
            timeProvider);

        var ncmProcessor = new NcmFileProcessor(
            new NcmDecoder(),
            sniffer,
            integrity.Object,
            atomicWriter,
            tagWriter.Object,
            coverWriter.Object,
            markerSerializer,
            mutexPool,
            logger,
            timeProvider);

        var router = new FileProcessorRouter(ucProcessor, ncmProcessor);
        var gate = new ResolvedMarkerGate(markerSerializer, logger, timeProvider);
        var orchestrator = new ScanOrchestrator(router, logger, gate);

        var options = new ScanOptions(new[] { _inputDir }, _outputDir, IntegrityLevel.Off, 1, CollisionPolicy.Skip)
        {
            Offline = true,
        };

        // --- Act ---
        var summary = await orchestrator.RunAsync(options);

        // --- Assert: BOTH processed Ok, one summary. ---
        summary.Processed.Should().Be(2);
        summary.Ok.Should().Be(2);
        summary.Errors.Should().Be(0);

        // .ncm produced "<artist> - <title>.flac" from EMBEDDED metadata.
        var expectedNcmOutput = Path.Combine(_outputDir, "Ncm Artist - Ncm Title.flac");
        File.Exists(expectedNcmOutput).Should().BeTrue("the .ncm must be decoded and named from its embedded metadata");

        // .uc produced its stem-based offline output (no API path).
        var expectedUcOutput = Path.Combine(_outputDir, "12345-mixed.mp3");
        File.Exists(expectedUcOutput).Should().BeTrue("the .uc must be decoded via the offline stem-namer path");

        // No cross-contamination: the .uc bytes round-trip the XOR fixture exactly
        // (it went through the XOR decoder, NOT the ncm decoder).
        var producedUc = await File.ReadAllBytesAsync(expectedUcOutput);
        var fixtureMp3 = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleMp3Path);
        producedUc.Should().Equal(fixtureMp3);

        // No cross-contamination: the .ncm never went through the API path — the
        // resolver was only ever consulted for the .uc source (offline fallback), and
        // NEVER for the .ncm source.
        metadataResolver.Verify(
            m => m.ResolveAsync(It.Is<string>(s => s.EndsWith("song.ncm")), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the .ncm path must not consult the NetEase metadata resolver");
    }
}
