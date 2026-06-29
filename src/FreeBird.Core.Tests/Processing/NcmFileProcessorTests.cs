using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Infrastructure;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using FreeBird.Core.Processing;
using FreeBird.Core.Tests.Fixtures;
using FreeBird.Core.Tests.Ncm;
using FreeBird.Core.Watch;
using Moq;
using Serilog;
using Xunit;

namespace FreeBird.Core.Tests.Processing;

/// <summary>
/// Tasks 10-13: end-to-end pipeline tests for <see cref="NcmFileProcessor"/>.
/// Uses the REAL NcmDecoder + MagicByteFormatSniffer + AtomicFileWriter +
/// ResolutionMarkerSerializer + OutputPathMutexPool (all cheap, in-process) and
/// Moq spies for the integrity / tag / cover collaborators (which would otherwise
/// shell out to flac / metaflac).
/// </summary>
public class NcmFileProcessorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-ncm-{Guid.NewGuid():N}");
    private readonly string _inputDir;
    private readonly string _outputDir;

    public NcmFileProcessorTests()
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

    // ---- Harness ---------------------------------------------------------

    private sealed class Harness
    {
        public required NcmFileProcessor Sut { get; init; }
        public required Mock<ICompositeIntegrityChecker> Integrity { get; init; }
        public required Mock<ITagWriter> TagWriter { get; init; }
        public required Mock<ICoverWriter> CoverWriter { get; init; }
    }

    private Harness MakeSut(IntegrityResult? integrityResult = null)
    {
        var decoder = new NcmDecoder();
        var sniffer = new MagicByteFormatSniffer();
        var writer = new AtomicFileWriter();
        var logger = new Mock<ILogger>().Object;
        var markerSerializer = new ResolutionMarkerSerializer(logger);
        var mutexPool = new OutputPathMutexPool();

        var integrity = new Mock<ICompositeIntegrityChecker>();
        integrity
            .Setup(i => i.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(integrityResult ?? IntegrityResult.Passed(IntegrityLevel.Off));

        var tagWriter = new Mock<ITagWriter>();
        tagWriter
            .Setup(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);

        var coverWriter = new Mock<ICoverWriter>();
        coverWriter
            .Setup(c => c.WriteCoverAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);

        var sut = new NcmFileProcessor(
            decoder, sniffer, integrity.Object, writer, tagWriter.Object, coverWriter.Object,
            markerSerializer, mutexPool, logger, TimeProvider.System);

        return new Harness { Sut = sut, Integrity = integrity, TagWriter = tagWriter, CoverWriter = coverWriter };
    }

    private async Task<string> WriteNcmAsync(string name, byte[] body, NcmFixtureMeta? meta, byte[]? cover)
    {
        var bytes = NcmTestEncoder.Encode(body, meta, cover);
        var path = Path.Combine(_inputDir, name);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private ScanOptions Options(bool writeTags = true, CollisionPolicy collision = CollisionPolicy.Skip)
        => new(new[] { _inputDir }, _outputDir, IntegrityLevel.Off, 1, collision) { WriteTags = writeTags };

    private static NcmFixtureMeta FlacMeta() =>
        new(MusicId: 111, MusicName: "Poison", Album: "Poison",
            Artists: new[] { ("Taylor Grey", 1L) }, Format: "flac");

    private static readonly byte[] SmallCover = { 0xFF, 0xD8, 0xFF, 0xE0, 0x10, 0x20, 0x30 }; // JPEG-ish

    // ---- Task 10: happy paths -------------------------------------------

    [Fact]
    public async Task ProcessAsync_FlacBody_WithMetadata_ReturnsOk_AndNamesFromMetadata()
    {
        var h = MakeSut();
        var body = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleFlacPath);
        var src = await WriteNcmAsync("song.ncm", body, FlacMeta(), SmallCover);

        var result = await h.Sut.ProcessAsync(src, Options());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        result.Format.Should().Be(AudioFormat.Flac);
        var expected = Path.Combine(_outputDir, "Taylor Grey - Poison.flac");
        File.Exists(expected).Should().BeTrue();
        result.OutputPath.Should().Be(expected);
    }

    [Fact]
    public async Task ProcessAsync_NullMetadata_FallsBackToSourceBasenameMinusNcm()
    {
        var h = MakeSut();
        var body = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleFlacPath);
        var src = await WriteNcmAsync("FallbackName.ncm", body, meta: null, cover: null);

        var result = await h.Sut.ProcessAsync(src, Options());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var expected = Path.Combine(_outputDir, "FallbackName.flac");
        File.Exists(expected).Should().BeTrue();
    }

    [Fact]
    public async Task ProcessAsync_Mp3Body_DetectsMp3()
    {
        var h = MakeSut();
        var body = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleMp3Path);
        var meta = new NcmFixtureMeta(222, "Track", "Album", new[] { ("Artist", 2L) }, "mp3");
        var src = await WriteNcmAsync("mp3song.ncm", body, meta, null);

        var result = await h.Sut.ProcessAsync(src, Options());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        result.Format.Should().Be(AudioFormat.Mp3);
        File.Exists(Path.Combine(_outputDir, "Artist - Track.mp3")).Should().BeTrue();
    }

    // ---- Task 11: quarantine + sidecar ----------------------------------

    [Fact]
    public async Task ProcessAsync_UnknownFormatBody_QuarantinesWithSidecar()
    {
        var h = MakeSut();
        var body = new byte[256];
        new Random(7).NextBytes(body); // non-audio garbage
        var src = await WriteNcmAsync("garbage.ncm", body, FlacMeta(), null);

        var result = await h.Sut.ProcessAsync(src, Options());

        result.Outcome.Should().Be(ScanOutcome.UnknownFormat);
        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        var sidecars = Directory.GetFiles(failedDir, "*.txt");
        sidecars.Should().ContainSingle();
        var content = await File.ReadAllTextAsync(sidecars[0]);
        content.Should().Contain("timestamp:");
        content.Should().Contain("source:");
        content.Should().Contain("format:");
        content.Should().Contain("integrity:");
        content.Should().Contain("reason:");
        content.Should().Contain("source_size:");
        content.Should().Contain("source_mtime:");
        content.Should().NotContain("error_class");
    }

    [Fact]
    public async Task ProcessAsync_BadMagic_WritesPermanentFailureSidecar_KeyedToSource()
    {
        var h = MakeSut();
        // Not a real .ncm — decoder throws NcmDecodeException on bad magic.
        var src = Path.Combine(_inputDir, "corrupt.ncm");
        await File.WriteAllBytesAsync(src, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        var result = await h.Sut.ProcessAsync(src, Options());

        result.Outcome.Should().Be(ScanOutcome.Error);

        // The watch loop's FilesystemSkipDecider globs ".freebird-failed/<stem>.*.txt"
        // and matches on source_size + source_mtime. A sidecar-only record suffices.
        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        Directory.Exists(failedDir).Should().BeTrue();
        var stem = StemBasedFileNamer.GetStem(src);
        var sidecars = Directory.GetFiles(failedDir, stem + ".*.txt");
        sidecars.Should().ContainSingle();

        var info = new FileInfo(src);
        var content = await File.ReadAllTextAsync(sidecars[0]);
        content.Should().Contain($"source_size: {info.Length}");
        // The FilesystemSkipDecider matches on BOTH source_size AND source_mtime; a
        // sidecar missing source_mtime would never be recognised as a permanent failure,
        // so the watch loop would re-decode the corrupt file every cycle.
        content.Should().Contain("source_mtime:");
    }

    [Fact]
    public async Task ProcessAsync_IntegrityFails_QuarantinesWithReason()
    {
        var h = MakeSut(IntegrityResult.Failed(IntegrityLevel.L1, "structural-corruption"));
        var body = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleFlacPath);
        var src = await WriteNcmAsync("bad.ncm", body, FlacMeta(), null);

        var result = await h.Sut.ProcessAsync(src, Options());

        result.Outcome.Should().Be(ScanOutcome.IntegrityFailed);
        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        var sidecars = Directory.GetFiles(failedDir, "*.txt");
        sidecars.Should().ContainSingle();
        (await File.ReadAllTextAsync(sidecars[0])).Should().Contain("structural-corruption");
    }

    // ---- Task 12: --no-write-tags suppresses tags AND cover -------------

    [Fact]
    public async Task ProcessAsync_NoWriteTags_SuppressesBothTagAndCoverWrites()
    {
        var h = MakeSut();
        var body = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleFlacPath);
        var src = await WriteNcmAsync("notags.ncm", body, FlacMeta(), SmallCover);

        var result = await h.Sut.ProcessAsync(src, Options(writeTags: false));

        result.Outcome.Should().Be(ScanOutcome.Ok);
        File.Exists(Path.Combine(_outputDir, "Taylor Grey - Poison.flac")).Should().BeTrue();
        h.TagWriter.Verify(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()), Times.Never);
        h.CoverWriter.Verify(c => c.WriteCoverAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WriteTags_InvokesTagWriterAndCoverWriter()
    {
        var h = MakeSut();
        var body = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleFlacPath);
        var src = await WriteNcmAsync("tags.ncm", body, FlacMeta(), SmallCover);

        var result = await h.Sut.ProcessAsync(src, Options(writeTags: true));

        result.Outcome.Should().Be(ScanOutcome.Ok);
        h.TagWriter.Verify(t => t.WriteAsync(
            It.IsAny<string>(),
            AudioFormat.Flac,
            It.Is<SongInfo>(s => s.Title == "Poison" && s.Artists.Contains("Taylor Grey") && s.Album == "Poison"),
            It.IsAny<CancellationToken>()), Times.Once);
        h.CoverWriter.Verify(c => c.WriteCoverAsync(
            It.IsAny<string>(),
            AudioFormat.Flac,
            It.Is<byte[]>(b => b.Length == SmallCover.Length),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Task 13: resolution marker with all 12 required fields ----------

    [Fact]
    public async Task ProcessAsync_Success_WritesMarker_WithAll12RequiredFields()
    {
        var h = MakeSut();
        var body = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleFlacPath);
        var src = await WriteNcmAsync("marker.ncm", body, FlacMeta(), SmallCover);

        var result = await h.Sut.ProcessAsync(src, Options());
        result.Outcome.Should().Be(ScanOutcome.Ok);

        var stem = StemBasedFileNamer.GetStem(src);
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, stem);
        File.Exists(markerPath).Should().BeTrue();

        var serializer = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        serializer.TryRead(markerPath, out var marker).Should().BeTrue();
        marker.Should().NotBeNull();
        marker!.Schema.Should().Be(2);
        marker.Status.Should().Be(MarkerStatus.Resolved);
        marker.MusicId.Should().Be("");
        marker.OutputName.Should().Be("Taylor Grey - Poison.flac");
        marker.Format.Should().Be("Flac");
        marker.Integrity.Should().NotBeNullOrEmpty();
        marker.NamingTemplate.Should().NotBeNullOrEmpty();
        marker.SourceStem.Should().Be(stem);
        marker.SourcePath.Should().Be(src);
        marker.SourceSize.Should().BeGreaterThan(0);
        marker.SourceMtime.Should().NotBe(default);
        marker.ResolvedAt.Should().NotBe(default);
    }
}
