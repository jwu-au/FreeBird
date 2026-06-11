using System;
using System.Globalization;
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
using FreeBird.Core.Provisioning;
using FreeBird.Core.Sidecar;
using Moq;
using Serilog;
using Fx = FreeBird.Core.Tests.Fixtures.Fixtures;

namespace FreeBird.Core.Tests.Processing;

public class FileProcessorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-proc-{Guid.NewGuid():N}");
    private readonly string _inputDir;
    private readonly string _outputDir;

    public FileProcessorTests()
    {
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // --- Helper: build the SUT with mocks; tests can override mocks per case ---

    private (FileProcessor sut,
             Mock<IXorDecoder> decoder,
             Mock<IFormatSniffer> sniffer,
             Mock<IFileNamer> naming,
             Mock<ICompositeIntegrityChecker> integrity,
             Mock<IAtomicFileWriter> writer,
             Mock<IMetadataResolver> metadata,
             Mock<ITagWriter> tagWriter) MakeMockedSut()
    {
        var decoder = new Mock<IXorDecoder>();
        var sniffer = new Mock<IFormatSniffer>();
        var naming = new Mock<IFileNamer>();
        var integrity = new Mock<ICompositeIntegrityChecker>();
        var writer = new Mock<IAtomicFileWriter>();
        var metadata = new Mock<IMetadataResolver>();
        var tagWriter = new Mock<ITagWriter>();
        // default tag writer: Success (no-op). Tests that exercise WriteTags=true must
        // set tagWriter behavior explicitly. Tests with WriteTags=false (the default
        // via DefaultOptions / ScanOptions ctor) never invoke this mock.
        tagWriter.Setup(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(TagWriteResult.Success.Instance);

        // default: writer simulates writing a small staging file by invoking the callback against a real FileStream
        writer.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<Func<Stream, CancellationToken, Task>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
              .Returns<string, Func<Stream, CancellationToken, Task>, bool, CancellationToken>(async (path, callback, _, ct) =>
              {
                  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                  await using var fs = File.Create(path);
                  await callback(fs, ct);
              });

        // default decoder: write a few bytes so file is non-empty (caller can override)
        decoder.Setup(d => d.DecodeAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
               .Returns<Stream, Stream, CancellationToken>(async (_, output, ct) =>
               {
                   await output.WriteAsync(new byte[] { 1, 2, 3, 4 }, ct);
               });

        // default metadata: Success with a stub song. v3.0.1 T04 requires that the
        // resolution outcome be writable to the JSON marker via MapToMarkerStatus.
        // The previous default `Fallback("offline-mode")` is reserved for the real
        // resolver's offline-flag branch and intentionally throws if mapped (the
        // invariant is "offline-mode reason ⇔ options.Offline=true"). Tests that
        // need a different outcome (fallback, deserialize-failure, success-with-tags)
        // override this setup explicitly.
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(0, "Stub", new[] { "Stub" })));

        var logger = new Mock<ILogger>().Object;
        // v3.0.1 T04: FileProcessor now writes a JSON resolution marker on the OK path.
        // Inject a REAL ResolutionMarkerSerializer so marker writes hit the same temp
        // _outputDir the tests already inspect; the serializer is stateless and writes
        // atomically to <outputDir>/.freebird-resolved/<stem>.json.
        var markerSerializer = new ResolutionMarkerSerializer(logger);
        var sut = new FileProcessor(decoder.Object, sniffer.Object, naming.Object, integrity.Object, writer.Object, metadata.Object, tagWriter.Object, markerSerializer, logger);
        return (sut, decoder, sniffer, naming, integrity, writer, metadata, tagWriter);
    }

    private async Task<string> MakeUcFileAsync(string name = "12345-test.uc")
    {
        var path = Path.Combine(_inputDir, name);
        await File.WriteAllBytesAsync(path, new byte[] { 0xEA, 0xE7, 0x90, 0x00 }); // pretend encrypted ID3
        return path;
    }

    private ScanOptions DefaultOptions(IntegrityLevel level = IntegrityLevel.Auto, CollisionPolicy collision = CollisionPolicy.Skip)
        => new(_inputDir, _outputDir, level, 1, collision);

    // --- Path 1: OK happy path ---

    [Fact]
    public async Task ProcessAsync_HappyPath_ReturnsOk_WritesFinalFile()
    {
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42-song.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Mp3, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("42-song.mp3");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        result.Format.Should().Be(AudioFormat.Mp3);
        result.OutputPath.Should().Be(Path.Combine(_outputDir, "42-song.mp3"));
        result.Integrity!.Ok.Should().BeTrue();
        File.Exists(Path.Combine(_outputDir, "42-song.mp3")).Should().BeTrue();
        // No staging leftovers
        Directory.GetFiles(Path.Combine(_outputDir, ".freebird-staging")).Should().BeEmpty();
    }

    // --- Path 2: Skipped (output already exists + Skip policy) ---

    [Fact]
    public async Task ProcessAsync_OutputExists_SkipPolicy_ReturnsSkipped_NoOverwrite()
    {
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync();

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Mp3, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("out.mp3");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));

        var existing = Path.Combine(_outputDir, "out.mp3");
        await File.WriteAllTextAsync(existing, "OLD");

        var result = await sut.ProcessAsync(ucPath, DefaultOptions(collision: CollisionPolicy.Skip));

        result.Outcome.Should().Be(ScanOutcome.Skipped);
        (await File.ReadAllTextAsync(existing)).Should().Be("OLD");
        // Staging cleaned up
        Directory.GetFiles(Path.Combine(_outputDir, ".freebird-staging")).Should().BeEmpty();
    }

    // --- Path 3: Overwrite ---

    [Fact]
    public async Task ProcessAsync_OutputExists_OverwritePolicy_OverwritesAndReturnsOk()
    {
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync();

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Mp3, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("out.mp3");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));

        var existing = Path.Combine(_outputDir, "out.mp3");
        await File.WriteAllTextAsync(existing, "OLD");

        var result = await sut.ProcessAsync(ucPath, DefaultOptions(collision: CollisionPolicy.Overwrite));

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var newBytes = await File.ReadAllBytesAsync(existing);
        newBytes.Should().Equal(new byte[] { 1, 2, 3, 4 }); // decoder default writes these
    }

    // --- Path 4: UnknownFormat → quarantine ---

    [Fact]
    public async Task ProcessAsync_UnknownFormat_Quarantines_WithSidecar()
    {
        var (sut, _, sniffer, _, _, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("weird.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Unknown);

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.UnknownFormat);
        result.Format.Should().Be(AudioFormat.Unknown);
        result.OutputPath.Should().NotBeNull().And.StartWith(Path.Combine(_outputDir, ".freebird-failed"));
        File.Exists(result.OutputPath!).Should().BeTrue();
        File.Exists(result.OutputPath + ".txt").Should().BeTrue();
        (await File.ReadAllTextAsync(result.OutputPath + ".txt")).Should().Contain("Unknown");
    }

    // --- Path 5: IntegrityFailed → quarantine ---

    [Fact]
    public async Task ProcessAsync_IntegrityFailed_QuarantinesWithReason_AndSidecar()
    {
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("99-bad.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Flac, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("99-bad.flac");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Failed(IntegrityLevel.L3, "PCM-MD5 mismatch"));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.IntegrityFailed);
        result.Integrity!.Ok.Should().BeFalse();
        result.Integrity.Reason.Should().Be("PCM-MD5 mismatch");
        File.Exists(result.OutputPath!).Should().BeTrue();
        File.Exists(result.OutputPath + ".txt").Should().BeTrue();
        (await File.ReadAllTextAsync(result.OutputPath + ".txt")).Should().Contain("PCM-MD5");
    }

    // --- v3.2 T01 (RED): IntegrityFailed quarantine must use SOURCE STEM, not musicId ---
    //
    // Bug: FileProcessor.cs:142 calls _naming.GetTargetName(..., metadata: null, ...) which under
    // v3 MetadataAwareFileNamer falls back to "{musicId}.{ext}". The watch-mode FilesystemSkipDecider
    // globs failed/ for "{stem}.*.txt" (see FilesystemSkipDecider.cs:160), so musicId-named sidecars
    // never match real NetEase-format source stems and the file is re-decoded on every poll cycle.
    //
    // The sibling UnknownFormat path at FileProcessor.cs:115 already does the right thing using
    // sourceStem. This test pins the fixed (stem-based) behavior; T02 will harmonize line 142.
    [Fact]
    public async Task Process_WhenIntegrityFailed_QuarantineFileUses_SourceStem_NotMusicId()
    {
        // Real NetEase cache format: <musicId>-<bitrate>-<md5hash>.uc!
        // The stem (everything before .uc!) is what FilesystemSkipDecider globs for.
        const string realNeteaseName = "3367798042-_-_5999-_-_a38658b6e504b7520bb4c507db13b9d2.uc!";
        const string expectedQuarantineBasename = "3367798042-_-_5999-_-_a38658b6e504b7520bb4c507db13b9d2.flac";

        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync(realNeteaseName);

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(AudioFormat.Flac);

        // Simulate the v3 MetadataAwareFileNamer's metadata-null fallback: "{musicId}.{ext}".
        // This is what the real namer returns on the IntegrityFailed path today, and it is the
        // root cause of the bug. The fix in T02 must NOT use this name for quarantine.
        naming.Setup(n => n.GetTargetName(
                    ucPath,
                    AudioFormat.Flac,
                    It.IsAny<FreeBird.Core.Metadata.SongInfo?>(),
                    It.IsAny<string?>()))
              .Returns("3367798042.flac");

        integrity.Setup(i => i.CheckAsync(
                    It.IsAny<string>(),
                    AudioFormat.Flac,
                    It.IsAny<IntegrityLevel>(),
                    It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Failed(IntegrityLevel.L1, "flac -t failed"));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        // (a) Outcome is IntegrityFailed
        result.Outcome.Should().Be(ScanOutcome.IntegrityFailed);

        // (b) Quarantined file path uses the SOURCE STEM (multi-bitrate safe) — NOT the bare musicId
        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        var expectedQuarantinePath = Path.Combine(failedDir, expectedQuarantineBasename);
        result.OutputPath.Should().Be(
            expectedQuarantinePath,
            because: "IntegrityFailed quarantine MUST be named with the source stem so the watch " +
                     "skip-decider stem-glob ({stem}.*.txt) finds the sidecar and prevents " +
                     "infinite re-decoding (FreeBird v3.2 fix).");

        // (c) Sidecar exists at OutputPath + ".txt"
        File.Exists(result.OutputPath + ".txt").Should().BeTrue(
            because: "Sidecar must be co-located with the quarantined file so the skip-decider " +
                     "glob '{stem}.*.txt' matches it.");

        // (d) Sanity: the buggy musicId-only quarantine path must NOT exist
        File.Exists(Path.Combine(failedDir, "3367798042.flac")).Should().BeFalse(
            because: "Quarantine naming must NOT fall back to '{musicId}.{ext}' — that asymmetry " +
                     "with the skip-decider's stem-glob is exactly the bug being fixed.");
    }


    // --- Path 6: Error (source not found) ---

    [Fact]
    public async Task ProcessAsync_SourceNotFound_ReturnsError()
    {
        var (sut, _, _, _, _, _, _, _) = MakeMockedSut();
        var result = await sut.ProcessAsync(Path.Combine(_inputDir, "missing.uc"), DefaultOptions());
        result.Outcome.Should().Be(ScanOutcome.Error);
        result.Reason.Should().Contain("not found");
    }

    // --- Cancellation ---

    [Fact]
    public async Task ProcessAsync_CancellationDuringDecrypt_PropagatesOCE_AndCleansStaging()
    {
        var (sut, decoder, _, _, _, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync();
        decoder.Setup(d => d.DecodeAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
               .Returns<Stream, Stream, CancellationToken>(async (_, _, ct) =>
               {
                   ct.ThrowIfCancellationRequested();
                   await Task.Yield();
               });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.ProcessAsync(ucPath, DefaultOptions(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // --- Constructor validation ---

    [Fact]
    public void Constructor_AnyNullDep_Throws()
    {
        var d = new Mock<IXorDecoder>().Object;
        var s = new Mock<IFormatSniffer>().Object;
        var n = new Mock<IFileNamer>().Object;
        var i = new Mock<ICompositeIntegrityChecker>().Object;
        var w = new Mock<IAtomicFileWriter>().Object;
        var m = new Mock<IMetadataResolver>().Object;
        var t = new Mock<ITagWriter>().Object;
        var l = new Mock<ILogger>().Object;
        var ms = new ResolutionMarkerSerializer(l);

        ((Action)(() => _ = new FileProcessor(null!, s, n, i, w, m, t, ms, l))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, null!, n, i, w, m, t, ms, l))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, s, null!, i, w, m, t, ms, l))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, s, n, null!, w, m, t, ms, l))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, s, n, i, null!, m, t, ms, l))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, s, n, i, w, null!, t, ms, l))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, s, n, i, w, m, null!, ms, l))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, s, n, i, w, m, t, null!, l))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, s, n, i, w, m, t, ms, null!))).Should().Throw<ArgumentNullException>();
    }

    // --- INTEGRATION test: real fixtures + real decoder/sniffer/naming/writer, mocked composite ---

    [Fact]
    public async Task ProcessAsync_RealMp3Fixture_EndToEnd_ProducesPlayableMp3()
    {
        // Build a real .uc from the real mp3 fixture
        var ucPath = await Fx.WriteFixtureAsUcAsync(Fx.SampleMp3Path, _inputDir, "77-end2end.uc");

        // Use REAL impls everywhere except integrity (mocked to always pass)
        var integrity = new Mock<ICompositeIntegrityChecker>();
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));

        // Real metadata resolver mocked to offline fallback so it never hits the network.
        var metadata = new Mock<IMetadataResolver>();
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("offline-mode"));

        var logger = new Mock<ILogger>().Object;
        var sut = new FileProcessor(
            new XorDecoder(),
            new MagicByteFormatSniffer(),
            new StemBasedFileNamer(),
            integrity.Object,
            new AtomicFileWriter(),
            metadata.Object,
            new Mock<ITagWriter>().Object,
            new ResolutionMarkerSerializer(logger),
            logger);

        // v3.0.1: Offline=true mirrors the mocked Fallback("offline-mode") resolver
        // and suppresses the JSON marker write (the marker invariant is "offline-mode
        // reason ⇔ Offline=true"). The original test purpose — no network calls — is
        // preserved.
        var opts = DefaultOptions() with { Offline = true };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        result.Format.Should().Be(AudioFormat.Mp3);
        result.OutputPath.Should().EndWith("77-end2end.mp3");

        // Decrypted file MUST equal the original fixture byte-for-byte (proves XOR roundtripped through the whole pipeline)
        var producedBytes = await File.ReadAllBytesAsync(result.OutputPath!);
        var fixtureBytes = await File.ReadAllBytesAsync(Fx.SampleMp3Path);
        producedBytes.Should().Equal(fixtureBytes);
    }

    [Fact]
    public async Task ProcessAsync_StagingFile_HasCorrectExtension_BeforeIntegrityCheck()
    {
        var capturedPath = string.Empty;
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42-song.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Mp3, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("42-song.mp3");

        // Capture the path that integrity sees
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .Callback<string, AudioFormat, IntegrityLevel, CancellationToken>((p, _, _, _) => capturedPath = p)
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        capturedPath.Should().EndWith(".mp3", "integrity must see staging file with proper extension for TagLib/flac to identify format");
    }

    // --- T15.6 D5 (extended for v2 T05): sidecar contains all 7 spec'd fields ---

    [Fact]
    public async Task ProcessAsync_QuarantineSidecar_ContainsAllSevenSpecFields()
    {
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("77-meta.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Flac, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("77-meta.flac");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Failed(IntegrityLevel.L3, "PCM-MD5 mismatch"));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions(IntegrityLevel.L3));

        result.Outcome.Should().Be(ScanOutcome.IntegrityFailed);
        var sidecar = await File.ReadAllTextAsync(result.OutputPath + ".txt");
        // All 5 v1 spec'd labels must appear
        sidecar.Should().Contain("timestamp:");
        sidecar.Should().Contain("source:");
        sidecar.Should().Contain("format:");
        sidecar.Should().Contain("integrity:");
        sidecar.Should().Contain("reason:");
        // v2 fields must also appear
        sidecar.Should().Contain("source_size:");
        sidecar.Should().Contain("source_mtime:");
        // And values
        sidecar.Should().Contain(ucPath);                 // source value
        sidecar.Should().Contain("Flac");                 // format value
        sidecar.Should().Contain("L3");                   // integrity value
        sidecar.Should().Contain("PCM-MD5 mismatch");     // reason value
    }

    // --- v2 T05 tests: source_size + source_mtime ---

    [Fact]
    public async Task ProcessAsync_QuarantineSidecar_SourceSize_MatchesActualBytes()
    {
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        // MakeUcFileAsync writes 4 bytes (0xEA, 0xE7, 0x90, 0x00)
        var ucPath = await MakeUcFileAsync("size-check.uc");
        var expectedSize = new FileInfo(ucPath).Length;
        expectedSize.Should().Be(4);

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Flac, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("size-check.flac");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Failed(IntegrityLevel.L1, "bad"));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.IntegrityFailed);
        var sidecar = await File.ReadAllTextAsync(result.OutputPath + ".txt");
        sidecar.Should().Contain($"source_size: {expectedSize}");
    }

    [Fact]
    public async Task ProcessAsync_QuarantineSidecar_SourceSize_IsBase10NoSeparators()
    {
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        // Write a larger payload so the size is > 1000 and would format with separators under some cultures
        var path = Path.Combine(_inputDir, "big.uc");
        await File.WriteAllBytesAsync(path, new byte[1234567]);

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetTargetName(path, AudioFormat.Mp3, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("big.mp3");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Failed(IntegrityLevel.L1, "bad"));

        var result = await sut.ProcessAsync(path, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.IntegrityFailed);
        var sidecar = await File.ReadAllTextAsync(result.OutputPath + ".txt");
        sidecar.Should().Contain("source_size: 1234567");
        // No thousands separators in any culture
        sidecar.Should().NotContain("1,234,567");
        sidecar.Should().NotContain("1.234.567");
    }

    [Fact]
    public async Task ProcessAsync_QuarantineSidecar_SourceMtime_IsIsoRoundTrip_WithUtcOffset()
    {
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("mtime-check.uc");
        var expectedMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(ucPath), TimeSpan.Zero);

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Mp3, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("mtime-check.mp3");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Failed(IntegrityLevel.L1, "bad"));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.IntegrityFailed);
        var sidecar = await File.ReadAllTextAsync(result.OutputPath + ".txt");
        // Parse out the source_mtime line, verify it round-trips back to a DateTimeOffset
        var mtimeLine = sidecar.Split('\n').FirstOrDefault(l => l.StartsWith("source_mtime:"));
        mtimeLine.Should().NotBeNull();
        var value = mtimeLine!.Substring("source_mtime:".Length).Trim();
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            .Should().BeTrue();
        // Must be UTC offset (the "o" format on a UTC DateTimeOffset emits +00:00)
        value.Should().EndWith("+00:00");
        // Should equal what we observed on disk (within ticks tolerance)
        parsed.Should().BeCloseTo(expectedMtime, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ProcessAsync_QuarantineSidecar_RoundTrip_ReadableByTextSidecarReader_AllSevenFieldsMatch()
    {
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("roundtrip.uc");
        var expectedSize = new FileInfo(ucPath).Length;
        var expectedMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(ucPath), TimeSpan.Zero);

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Flac, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("roundtrip.flac");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Failed(IntegrityLevel.L3, "PCM-MD5 mismatch"));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions(IntegrityLevel.L3));

        result.Outcome.Should().Be(ScanOutcome.IntegrityFailed);

        var reader = new TextSidecarReader();
        var record = await reader.TryReadAsync(result.OutputPath + ".txt");

        record.Should().NotBeNull();
        record!.Source.Should().Be(ucPath);
        record.Format.Should().Be(AudioFormat.Flac);
        record.IntegrityLevel.Should().Be(IntegrityLevel.L3);
        record.Reason.Should().Be("PCM-MD5 mismatch");
        record.SourceSize.Should().Be(expectedSize);
        record.SourceMtime.Should().NotBeNull();
        record.SourceMtime!.Value.Should().BeCloseTo(expectedMtime, TimeSpan.FromMilliseconds(1));
        // Timestamp must be a recent UTC time (within the last minute)
        record.Timestamp.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void BuildSidecarContent_SourceMissing_WritesZeroSizeEpochMtime()
    {
        // Test the internal sidecar builder directly with a non-existent source path.
        var missing = Path.Combine(_tempDir, "definitely-not-there.uc");
        File.Exists(missing).Should().BeFalse();

        var content = FileProcessor.BuildSidecarContentForTesting(
            missing, AudioFormat.Mp3, IntegrityLevel.L1, "test reason",
            new Mock<ILogger>().Object);

        content.Should().Contain("source_size: 0");
        content.Should().Contain("source_mtime: 1970-01-01T00:00:00.0000000+00:00");
    }

    [Fact]
    public void BuildSidecarContent_SourceMissing_LogsWarning()
    {
        var missing = Path.Combine(_tempDir, "vanished.uc");
        var logger = new Mock<ILogger>();

        _ = FileProcessor.BuildSidecarContentForTesting(
            missing, AudioFormat.Mp3, IntegrityLevel.L1, "r", logger.Object);

        // The Serilog call site is Warning<string>(string template, string propertyValue) — the
        // generic single-arg overload. Verify against that exact shape.
        logger.Verify(
            l => l.Warning<string>(
                It.Is<string>(s => s.Contains("vanished")),
                It.IsAny<string>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void BuildSidecarContent_FieldsAppearInExpectedOrder()
    {
        var path = Path.Combine(_tempDir, "order.uc");
        File.WriteAllBytes(path, new byte[42]);

        var content = FileProcessor.BuildSidecarContentForTesting(
            path, AudioFormat.Flac, IntegrityLevel.L3, "reason here",
            new Mock<ILogger>().Object);

        var idxTimestamp    = content.IndexOf("timestamp:", StringComparison.Ordinal);
        var idxSource       = content.IndexOf("source:", StringComparison.Ordinal);
        var idxFormat       = content.IndexOf("format:", StringComparison.Ordinal);
        var idxIntegrity    = content.IndexOf("integrity:", StringComparison.Ordinal);
        var idxReason       = content.IndexOf("reason:", StringComparison.Ordinal);
        var idxSourceSize   = content.IndexOf("source_size:", StringComparison.Ordinal);
        var idxSourceMtime  = content.IndexOf("source_mtime:", StringComparison.Ordinal);

        // All present
        idxTimestamp.Should().BeGreaterThanOrEqualTo(0);
        idxSource.Should().BeGreaterThan(idxTimestamp);
        idxFormat.Should().BeGreaterThan(idxSource);
        idxIntegrity.Should().BeGreaterThan(idxFormat);
        idxReason.Should().BeGreaterThan(idxIntegrity);
        idxSourceSize.Should().BeGreaterThan(idxReason);
        idxSourceMtime.Should().BeGreaterThan(idxSourceSize);
    }

    // --- v3.2 T04: schema version field (forward-compat) ---

    [Fact]
    public void BuildSidecarContent_IncludesVersion3LineAfterTimestamp()
    {
        var path = Path.Combine(_tempDir, "v3.uc");
        File.WriteAllBytes(path, new byte[16]);

        var content = FileProcessor.BuildSidecarContentForTesting(
            path, AudioFormat.Flac, IntegrityLevel.L3, "any reason",
            new Mock<ILogger>().Object);

        // The literal `version: 3` line must appear and must come directly after the
        // `timestamp:` line (no other field between them). Use line-based assertion so
        // the test is platform-agnostic (\r\n on Windows, \n elsewhere).
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var idxTimestampLine = Array.FindIndex(lines, l => l.StartsWith("timestamp:", StringComparison.Ordinal));
        var idxVersionLine   = Array.FindIndex(lines, l => l.StartsWith("version:",   StringComparison.Ordinal));
        var idxSourceLine    = Array.FindIndex(lines, l => l.StartsWith("source:",    StringComparison.Ordinal));

        idxTimestampLine.Should().BeGreaterThanOrEqualTo(0);
        idxVersionLine.Should().Be(idxTimestampLine + 1, "version line must immediately follow timestamp line");
        idxSourceLine.Should().Be(idxVersionLine + 1, "source line must follow version line");
        lines[idxVersionLine].Should().Be("version: 3");
    }

    // --- T15.6 D6: .uc! quarantine name strips suffix correctly ---

    [Fact]
    public async Task ProcessAsync_UnknownFormat_UcBangSuffix_QuarantineNameStripsCorrectly()
    {
        var (sut, _, sniffer, _, _, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("99-mac.uc!");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Unknown);

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.UnknownFormat);
        // BUG (pre-fix): produced "99-mac.uc.bin" — must now be "99-mac.bin"
        Path.GetFileName(result.OutputPath!).Should().Be("99-mac.bin");
    }

    // --- T15.6 C1: sidecar.tmp is not left behind after successful quarantine ---

    [Fact]
    public async Task ProcessAsync_QuarantineSidecarTmp_NotLeftBehind_AfterSuccess()
    {
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("xx-fail.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Mp3, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("xx-fail.mp3");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Failed(IntegrityLevel.L1, "structural failure"));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.IntegrityFailed);
        var failedDir = Path.Combine(_outputDir, ".freebird-failed");
        Directory.Exists(failedDir).Should().BeTrue();
        var files = Directory.GetFiles(failedDir);
        // Expect exactly 2 files: the quarantined audio + its sidecar .txt. No .tmp leftovers.
        files.Should().HaveCount(2);
        files.Should().Contain(f => f.EndsWith(".mp3"));
        files.Should().Contain(f => f.EndsWith(".mp3.txt"));
        files.Should().NotContain(f => f.EndsWith(".tmp"));
    }

    [Fact]
    public async Task ProcessAsync_FlacStaging_HasFlacExtension_BeforeIntegrityCheck()
    {
        var capturedPath = string.Empty;
        var (sut, _, sniffer, naming, integrity, _, _, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("99-song.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(AudioFormat.Flac);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Flac, It.IsAny<FreeBird.Core.Metadata.SongInfo?>(), It.IsAny<string?>())).Returns("99-song.flac");

        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .Callback<string, AudioFormat, IntegrityLevel, CancellationToken>((p, _, _, _) => capturedPath = p)
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L3));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        capturedPath.Should().EndWith(".flac");
    }

    // -------------------------------------------------------------------------
    // v3 T14: metadata resolve + rename + OK-path sidecar
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_WithMetadataSuccess_RenamesUsingArtistTitle()
    {
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(42, "My Title", new[] { "My Artist" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.Is<SongInfo?>(s => s != null && s.Title == "My Title"), It.IsAny<string?>()))
              .Returns("My Artist - My Title.flac");

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        File.Exists(Path.Combine(_outputDir, "My Artist - My Title.flac")).Should().BeTrue();
        // Success → no sidecar
        File.Exists(Path.Combine(_outputDir, "My Artist - My Title.flac.txt")).Should().BeFalse();
        naming.Verify(
            n => n.GetTargetName(ucPath, AudioFormat.Flac, It.Is<SongInfo?>(s => s != null && s.MusicId == 42), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithMetadataFallback_NonOffline_WritesMarkerWithReason()
    {
        // v3.0.1 T04: metadata fallback no longer writes a .flac.txt sidecar;
        // the outcome is now recorded in the JSON resolution marker under
        // <outputDir>/.freebird-resolved/<stem>.json with Status=MetadataFetchFailed.
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("metadata-fetch-failed"));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, (SongInfo?)null, It.IsAny<string?>()))
              .Returns("42.flac");

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        File.Exists(Path.Combine(_outputDir, "42.flac")).Should().BeTrue();
        // Legacy .flac.txt sidecar is gone in v3.0.1.
        File.Exists(Path.Combine(_outputDir, "42.flac.txt")).Should().BeFalse();
        // JSON marker is written with the fallback reason.
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        File.Exists(markerPath).Should().BeTrue();
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.Status.Should().Be(MarkerStatus.MetadataFetchFailed);
        marker.Reason.Should().Be("metadata-fetch-failed");
        marker.SourcePath.Should().Be(ucPath);
        marker.Format.Should().Be("Flac");
        marker.OutputName.Should().Be("42.flac");
    }

    [Fact]
    public async Task ProcessAsync_WithOfflineFallback_NoSidecarWritten()
    {
        // Spec §10 + v3.0.1 D5: when Offline=true, the resolver returns
        // Fallback("offline-mode") AND the JSON marker write is suppressed.
        // The legacy .flac.txt OK-path sidecar is also gone (T04).
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("offline-mode"));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, (SongInfo?)null, It.IsAny<string?>()))
              .Returns("42.flac");

        var opts = DefaultOptions() with { Offline = true };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        File.Exists(Path.Combine(_outputDir, "42.flac")).Should().BeTrue();
        // Spec §10 special case: offline-mode never emits a .flac.txt sidecar.
        File.Exists(Path.Combine(_outputDir, "42.flac.txt")).Should().BeFalse();
        // v3.0.1 T04 / D5: Offline=true also suppresses the JSON resolution marker.
        var markerDir = Path.Combine(_outputDir, ResolutionMarkerSerializer.MarkerSubdir);
        if (Directory.Exists(markerDir))
        {
            Directory.GetFiles(markerDir, "*.json").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ProcessAsync_WithDeserializeFailure_WritesMarkerReason()
    {
        // v3.0.1 T04: deserialize-failure is captured in the JSON marker now.
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("metadata-deserialize-failed"));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, (SongInfo?)null, It.IsAny<string?>()))
              .Returns("42.flac");

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        File.Exists(Path.Combine(_outputDir, "42.flac.txt")).Should().BeFalse();
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        File.Exists(markerPath).Should().BeTrue();
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.Status.Should().Be(MarkerStatus.MetadataDeserializeFailed);
        marker.Reason.Should().Be("metadata-deserialize-failed");
    }

    [Fact]
    public async Task Process_PassesPerRunTemplate_ToNamer()
    {
        // v3 T19a: closes the bridge gap. ScanOptions.NamingTemplate now flows
        // through FileProcessor.ProcessAsync into IFileNamer.GetTargetName as the
        // 4th positional argument (the optional `namingTemplate` parameter).
        //
        // This test pins down the plumbing contract: whatever per-run template the
        // caller sets MUST be the exact string the namer receives — no default
        // substitution by the orchestrator.
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("1.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(1, "T", new[] { "A" }, Album: "AL")));
        // The mock returns a fixed name regardless of inputs — we only care that
        // the 4th argument (per-run template) matches the per-run options string.
        // NOTE: filename uses '-' (not '|') because '|' is a reserved character
        // on Windows NTFS and File.Create would throw. The TEMPLATE itself can
        // still contain '|' since it's never written to disk.
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("AL-T.flac");

        const string PerRunTemplate = "{album}|{title}";
        var opts = new ScanOptions(_inputDir, _outputDir) { NamingTemplate = PerRunTemplate };

        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        File.Exists(Path.Combine(_outputDir, "AL-T.flac")).Should().BeTrue();
        naming.Verify(
            n => n.GetTargetName(ucPath, AudioFormat.Flac, It.Is<SongInfo?>(s => s != null && s.MusicId == 1), PerRunTemplate),
            Times.Once);
    }

    // -------------------------------------------------------------------------
    // v3 T18: post-rename tag-write integration
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_WriteTagsFalse_DoesNotInvokeTagger()
    {
        // v3.3: WriteTags defaults to TRUE; this test explicitly opts out via
        // `with { WriteTags = false }` to validate the tagger mock is never hit
        // when the user has disabled tag writing.
        var (sut, _, sniffer, naming, integrity, _, metadata, tagWriter) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(42, "T", new[] { "A" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("A - T.flac");

        // v3.3: must explicitly opt out — default flipped to true in v3.3.
        var opts = DefaultOptions() with { WriteTags = false };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        File.Exists(Path.Combine(_outputDir, "A - T.flac")).Should().BeTrue();
        File.Exists(Path.Combine(_outputDir, "A - T.flac.txt")).Should().BeFalse();
        tagWriter.Verify(
            t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WriteTagsTrue_AndMetadataSuccess_InvokesTagger()
    {
        var (sut, _, sniffer, naming, integrity, _, metadata, tagWriter) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        var song = new SongInfo(42, "My Title", new[] { "My Artist" });
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(song));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("My Artist - My Title.flac");

        var opts = new ScanOptions(_inputDir, _outputDir) { WriteTags = true };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var expectedFinal = Path.Combine(_outputDir, "My Artist - My Title.flac");
        File.Exists(expectedFinal).Should().BeTrue();
        File.Exists(expectedFinal + ".txt").Should().BeFalse();   // success path → no sidecar
        tagWriter.Verify(
            t => t.WriteAsync(expectedFinal, AudioFormat.Flac, It.Is<SongInfo>(s => s.MusicId == 42 && s.Title == "My Title"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WriteTagsTrue_ButMetadataFallback_DoesNotInvokeTagger()
    {
        // When metadata fell back, there's no SongInfo to write — the tagger must
        // never be called. v3.0.1 T04: the fallback outcome is now recorded in
        // the JSON marker (Status=MetadataFetchFailed, TagWriteStatus="skipped"),
        // not in a .flac.txt sidecar.
        var (sut, _, sniffer, naming, integrity, _, metadata, tagWriter) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("metadata-fetch-failed"));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, (SongInfo?)null, It.IsAny<string?>()))
              .Returns("42.flac");

        var opts = new ScanOptions(_inputDir, _outputDir) { WriteTags = true };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        File.Exists(Path.Combine(_outputDir, "42.flac")).Should().BeTrue();
        // v3.0.1: metadata-fallback no longer emits .flac.txt; it's in the JSON marker.
        File.Exists(Path.Combine(_outputDir, "42.flac.txt")).Should().BeFalse();
        tagWriter.Verify(
            t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_TagWriteFails_WritesMarkerWithTagWriteFailed_ButKeepsFile()
    {
        // CRITICAL CONTRACT: tag-write failure must NEVER delete or roll back the
        // decoded audio file. The file is the user's primary artifact; the marker
        // is supplementary.
        // v3.0.1 T04: tag-write outcomes now live in the JSON marker via
        // TagWriteStatus="failed" + TagWriteReason="tag-tool-missing".
        var (sut, _, sniffer, naming, integrity, _, metadata, tagWriter) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(42, "T", new[] { "A" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("A - T.flac");
        tagWriter.Setup(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new TagWriteResult.Failed("tag-tool-missing"));

        var opts = new ScanOptions(_inputDir, _outputDir) { WriteTags = true };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var expectedFinal = Path.Combine(_outputDir, "A - T.flac");
        File.Exists(expectedFinal).Should().BeTrue();   // FILE PRESERVED — non-negotiable
        File.Exists(expectedFinal + ".txt").Should().BeFalse();   // legacy sidecar gone
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        File.Exists(markerPath).Should().BeTrue();
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.Status.Should().Be(MarkerStatus.Resolved);
        marker.TagWriteStatus.Should().Be("failed");
        marker.TagWriteReason.Should().Be("tag-tool-missing");
    }

    [Fact]
    public async Task ProcessAsync_TagWriteThrows_StillKeepsFile_AndWritesMarkerWithTagWriteFailed()
    {
        // Defensive: an UNEXPECTED throw from the tagger (not a Failed result) still
        // must not lose the file. The catch-all maps to TagWriteReason="tag-write-failed".
        var (sut, _, sniffer, naming, integrity, _, metadata, tagWriter) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(42, "T", new[] { "A" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("A - T.flac");
        tagWriter.Setup(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("tagger lib panicked"));

        var opts = new ScanOptions(_inputDir, _outputDir) { WriteTags = true };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var expectedFinal = Path.Combine(_outputDir, "A - T.flac");
        File.Exists(expectedFinal).Should().BeTrue();   // FILE PRESERVED
        File.Exists(expectedFinal + ".txt").Should().BeFalse();   // legacy sidecar gone
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        File.Exists(markerPath).Should().BeTrue();
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.Status.Should().Be(MarkerStatus.Resolved);
        marker.TagWriteStatus.Should().Be("failed");
        marker.TagWriteReason.Should().Be("tag-write-failed");
    }

    [Fact]
    public async Task ProcessAsync_TagWriterThrowsFlacNotAvailable_RecordsTagToolMissingReason_FileNotKilled()
    {
        // v3.1 T13: When the tag writer throws FlacNotAvailableException (metaflac
        // missing — race after startup probe, or on a system where probe was skipped),
        // we MUST convert to TagWriteResult.Failed("tag-tool-missing") and preserve
        // the decoded audio. Tag-write is decorative; it never kills the file.
        var (sut, _, sniffer, naming, integrity, _, metadata, tagWriter) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(42, "T", new[] { "A" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("A - T.flac");
        tagWriter.Setup(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new FlacNotAvailableException("metaflac not on PATH"));

        var opts = new ScanOptions(_inputDir, _outputDir) { WriteTags = true };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var expectedFinal = Path.Combine(_outputDir, "A - T.flac");
        File.Exists(expectedFinal).Should().BeTrue();   // FILE PRESERVED — non-negotiable
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        File.Exists(markerPath).Should().BeTrue();
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.Status.Should().Be(MarkerStatus.Resolved);
        marker.TagWriteStatus.Should().Be("failed");
        marker.TagWriteReason.Should().Be("tag-tool-missing");
    }

    // -------------------------------------------------------------------------
    // v3.0.1 T04: JSON resolution marker tests (per task spec; 11 new tests)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessAsync_SuccessfulResolution_WritesResolvedMarker()
    {
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L3));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(42, "My Title", new[] { "My Artist" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("My Artist - My Title.flac");

        // v3.3: opt out of tag writing here so TagWriteStatus stays null and we
        // can assert the resolved marker schema independent of the new default.
        var result = await sut.ProcessAsync(ucPath, DefaultOptions() with { WriteTags = false });

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        File.Exists(markerPath).Should().BeTrue();
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.Status.Should().Be(MarkerStatus.Resolved);
        marker.OutputName.Should().Be("My Artist - My Title.flac");
        marker.Reason.Should().BeNull();
        marker.RetryAfter.Should().BeNull();
        marker.TagWriteStatus.Should().BeNull();
        marker.TagWriteReason.Should().BeNull();
        marker.MusicId.Should().Be("42");
        marker.SourceStem.Should().Be("42");
    }

    [Fact]
    public async Task ProcessAsync_MetadataEmpty_WritesMarkerWith7dRetry()
    {
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("metadata-empty"));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, (SongInfo?)null, It.IsAny<string?>()))
              .Returns("42.flac");

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        File.Exists(markerPath).Should().BeTrue();
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.Status.Should().Be(MarkerStatus.MetadataEmpty);
        marker.Reason.Should().Be("metadata-empty");
        marker.RetryAfter.Should().NotBeNull();
        var actualDelay = (marker.RetryAfter!.Value - marker.ResolvedAt).TotalSeconds;
        var expectedDelay = TimeSpan.FromDays(7).TotalSeconds;
        actualDelay.Should().BeApproximately(expectedDelay, 2.0);
    }

    [Fact]
    public async Task ProcessAsync_MetadataFetchFailed_WritesMarkerWith1hRetry()
    {
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("metadata-fetch-failed"));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, (SongInfo?)null, It.IsAny<string?>()))
              .Returns("42.flac");

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.Status.Should().Be(MarkerStatus.MetadataFetchFailed);
        marker.Reason.Should().Be("metadata-fetch-failed");
        var actualDelay = (marker.RetryAfter!.Value - marker.ResolvedAt).TotalSeconds;
        actualDelay.Should().BeApproximately(TimeSpan.FromHours(1).TotalSeconds, 2.0);
    }

    [Fact]
    public async Task ProcessAsync_MetadataDeserializeFailed_WritesMarkerWith24hRetry()
    {
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("metadata-deserialize-failed"));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, (SongInfo?)null, It.IsAny<string?>()))
              .Returns("42.flac");

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.Status.Should().Be(MarkerStatus.MetadataDeserializeFailed);
        marker.Reason.Should().Be("metadata-deserialize-failed");
        var actualDelay = (marker.RetryAfter!.Value - marker.ResolvedAt).TotalSeconds;
        actualDelay.Should().BeApproximately(TimeSpan.FromHours(24).TotalSeconds, 2.0);
    }

    [Fact]
    public async Task ProcessAsync_OfflineMode_DoesNotWriteMarker()
    {
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("offline-mode"));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, (SongInfo?)null, It.IsAny<string?>()))
              .Returns("42.flac");

        var opts = DefaultOptions() with { Offline = true };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var markerDir = Path.Combine(_outputDir, ResolutionMarkerSerializer.MarkerSubdir);
        if (Directory.Exists(markerDir))
        {
            Directory.GetFiles(markerDir, "*.json").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ProcessAsync_OfflineMode_DoesNotDeleteExistingMarker()
    {
        // Pre-seed a marker; offline run must not touch it.
        var preSeed = new ResolutionMarker
        {
            Schema = 1,
            SourceStem = "42",
            MusicId = "42",
            SourcePath = "/old/path.uc",
            SourceSize = 1024L,
            SourceMtime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ResolvedAt = new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero),
            Status = MarkerStatus.Resolved,
            OutputName = "old.flac",
            Format = "Flac",
            Integrity = "L1",
            NamingTemplate = "{artist} - {title}",
        };
        var preSeedSer = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        preSeedSer.WriteAtomic(_outputDir, preSeed);
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        var preSeedBytes = await File.ReadAllBytesAsync(markerPath);

        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("offline-mode"));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, (SongInfo?)null, It.IsAny<string?>()))
              .Returns("42.flac");

        var opts = DefaultOptions() with { Offline = true };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        File.Exists(markerPath).Should().BeTrue();
        var afterBytes = await File.ReadAllBytesAsync(markerPath);
        afterBytes.Should().Equal(preSeedBytes, "offline mode must NEVER mutate an existing marker");
    }

    [Fact]
    public async Task ProcessAsync_MarkerSourceStemMatchesStemBasedFileNamer()
    {
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var compositeName = "3367798042-_-_5999-_-_abc.uc!";
        var ucPath = Path.Combine(_inputDir, compositeName);
        await File.WriteAllBytesAsync(ucPath, new byte[] { 0xEA, 0xE7, 0x90, 0x00 });

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(3367798042L, "T", new[] { "A" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("A - T.flac");

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var expectedStem = StemBasedFileNamer.GetStem(ucPath);
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, expectedStem);
        File.Exists(markerPath).Should().BeTrue();
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.SourceStem.Should().Be(expectedStem);
        marker.MusicId.Should().Be("3367798042");
    }

    [Fact]
    public async Task ProcessAsync_CollisionSkip_WritesResolvedMarkerWithExistingFilename()
    {
        // D7(a): on collision-skip we keep the existing file untouched but still
        // write a marker so the watch decider treats this source as resolved.
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(42, "T", new[] { "A" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("A - T.flac");

        var existing = Path.Combine(_outputDir, "A - T.flac");
        var preSeedBytes = new byte[] { 0xAB, 0xCD, 0xEF };
        await File.WriteAllBytesAsync(existing, preSeedBytes);

        var result = await sut.ProcessAsync(ucPath, DefaultOptions(collision: CollisionPolicy.Skip));

        result.Outcome.Should().Be(ScanOutcome.Skipped);
        var afterBytes = await File.ReadAllBytesAsync(existing);
        afterBytes.Should().Equal(preSeedBytes, "collision-skip must not overwrite the existing file");

        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        File.Exists(markerPath).Should().BeTrue();
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.Status.Should().Be(MarkerStatus.Resolved);
        marker.OutputName.Should().Be("A - T.flac");
    }

    [Fact]
    public async Task ProcessAsync_AudioFileMoveFails_NoMarkerWritten()
    {
        // Create a DIRECTORY at the final path so File.Move(staging, final) throws.
        // The marker write must be unreachable (ordering invariant: marker after move).
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(42, "T", new[] { "A" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("collision-dir.flac");

        // Pre-create a DIRECTORY at the target path so File.Move throws.
        // Use CollisionPolicy.Overwrite so the existence check doesn't short-circuit to Skipped.
        Directory.CreateDirectory(Path.Combine(_outputDir, "collision-dir.flac"));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions(collision: CollisionPolicy.Overwrite));

        result.Outcome.Should().Be(ScanOutcome.Error);
        var markerDir = Path.Combine(_outputDir, ResolutionMarkerSerializer.MarkerSubdir);
        if (Directory.Exists(markerDir))
        {
            Directory.GetFiles(markerDir, "*.json").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ProcessAsync_WriteTagsTrue_TagWriteSucceeds_MarkerHasTagWriteStatusOk()
    {
        var (sut, _, sniffer, naming, integrity, _, metadata, tagWriter) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(42, "T", new[] { "A" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("A - T.flac");
        // tagWriter default already returns TagWriteResult.Success.Instance.

        var opts = new ScanOptions(_inputDir, _outputDir) { WriteTags = true };
        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        File.Exists(markerPath).Should().BeTrue();
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.TagWriteStatus.Should().Be("ok");
        marker.TagWriteReason.Should().BeNull();
    }

    [Fact]
    public async Task ProcessAsync_WriteTagsFalse_MarkerHasTagWriteStatusNull()
    {
        var (sut, _, sniffer, naming, integrity, _, metadata, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));
        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Success(new SongInfo(42, "T", new[] { "A" })));
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("A - T.flac");

        // v3.3: must explicitly opt out — default flipped to true in v3.3.
        var result = await sut.ProcessAsync(ucPath, DefaultOptions() with { WriteTags = false });

        result.Outcome.Should().Be(ScanOutcome.Ok);
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "42");
        var ser = new ResolutionMarkerSerializer(new Mock<ILogger>().Object);
        ser.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.TagWriteStatus.Should().BeNull();
        marker.TagWriteReason.Should().BeNull();
    }
}
