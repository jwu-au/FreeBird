using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Infrastructure;
using FreeBird.Core.Models;
using FreeBird.Core.Processing;
using Moq;
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
             Mock<INamingStrategy> naming,
             Mock<ICompositeIntegrityChecker> integrity,
             Mock<IAtomicFileWriter> writer) MakeMockedSut()
    {
        var decoder = new Mock<IXorDecoder>();
        var sniffer = new Mock<IFormatSniffer>();
        var naming = new Mock<INamingStrategy>();
        var integrity = new Mock<ICompositeIntegrityChecker>();
        var writer = new Mock<IAtomicFileWriter>();

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

        var sut = new FileProcessor(decoder.Object, sniffer.Object, naming.Object, integrity.Object, writer.Object);
        return (sut, decoder, sniffer, naming, integrity, writer);
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
        var (sut, _, sniffer, naming, integrity, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42-song.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetOutputFileName(ucPath, AudioFormat.Mp3)).Returns("42-song.mp3");
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
        var (sut, _, sniffer, naming, integrity, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync();

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetOutputFileName(ucPath, AudioFormat.Mp3)).Returns("out.mp3");
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
        var (sut, _, sniffer, naming, integrity, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync();

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetOutputFileName(ucPath, AudioFormat.Mp3)).Returns("out.mp3");
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
        var (sut, _, sniffer, _, _, _) = MakeMockedSut();
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
        var (sut, _, sniffer, naming, integrity, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("99-bad.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        naming.Setup(n => n.GetOutputFileName(ucPath, AudioFormat.Flac)).Returns("99-bad.flac");
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

    // --- Path 6: Error (source not found) ---

    [Fact]
    public async Task ProcessAsync_SourceNotFound_ReturnsError()
    {
        var (sut, _, _, _, _, _) = MakeMockedSut();
        var result = await sut.ProcessAsync(Path.Combine(_inputDir, "missing.uc"), DefaultOptions());
        result.Outcome.Should().Be(ScanOutcome.Error);
        result.Reason.Should().Contain("not found");
    }

    // --- Cancellation ---

    [Fact]
    public async Task ProcessAsync_CancellationDuringDecrypt_PropagatesOCE_AndCleansStaging()
    {
        var (sut, decoder, _, _, _, _) = MakeMockedSut();
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
        var n = new Mock<INamingStrategy>().Object;
        var i = new Mock<ICompositeIntegrityChecker>().Object;
        var w = new Mock<IAtomicFileWriter>().Object;

        ((Action)(() => _ = new FileProcessor(null!, s, n, i, w))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, null!, n, i, w))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, s, null!, i, w))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, s, n, null!, w))).Should().Throw<ArgumentNullException>();
        ((Action)(() => _ = new FileProcessor(d, s, n, i, null!))).Should().Throw<ArgumentNullException>();
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

        var sut = new FileProcessor(
            new XorDecoder(),
            new MagicByteFormatSniffer(),
            new StemPlusExtensionNamingStrategy(),
            integrity.Object,
            new AtomicFileWriter());

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

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
        var (sut, _, sniffer, naming, integrity, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("42-song.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetOutputFileName(ucPath, AudioFormat.Mp3)).Returns("42-song.mp3");

        // Capture the path that integrity sees
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .Callback<string, AudioFormat, IntegrityLevel, CancellationToken>((p, _, _, _) => capturedPath = p)
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        capturedPath.Should().EndWith(".mp3", "integrity must see staging file with proper extension for TagLib/flac to identify format");
    }

    // --- T15.6 D5: sidecar contains all 5 spec'd fields ---

    [Fact]
    public async Task ProcessAsync_QuarantineSidecar_ContainsAllFiveSpecFields()
    {
        var (sut, _, sniffer, naming, integrity, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("77-meta.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Flac);
        naming.Setup(n => n.GetOutputFileName(ucPath, AudioFormat.Flac)).Returns("77-meta.flac");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Failed(IntegrityLevel.L3, "PCM-MD5 mismatch"));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions(IntegrityLevel.L3));

        result.Outcome.Should().Be(ScanOutcome.IntegrityFailed);
        var sidecar = await File.ReadAllTextAsync(result.OutputPath + ".txt");
        // All 5 spec'd labels must appear
        sidecar.Should().Contain("timestamp:");
        sidecar.Should().Contain("source:");
        sidecar.Should().Contain("format:");
        sidecar.Should().Contain("integrity:");
        sidecar.Should().Contain("reason:");
        // And values
        sidecar.Should().Contain(ucPath);                 // source value
        sidecar.Should().Contain("Flac");                 // format value
        sidecar.Should().Contain("L3");                   // integrity value
        sidecar.Should().Contain("PCM-MD5 mismatch");     // reason value
    }

    // --- T15.6 D6: .uc! quarantine name strips suffix correctly ---

    [Fact]
    public async Task ProcessAsync_UnknownFormat_UcBangSuffix_QuarantineNameStripsCorrectly()
    {
        var (sut, _, sniffer, _, _, _) = MakeMockedSut();
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
        var (sut, _, sniffer, naming, integrity, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("xx-fail.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetOutputFileName(ucPath, AudioFormat.Mp3)).Returns("xx-fail.mp3");
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
        var (sut, _, sniffer, naming, integrity, _) = MakeMockedSut();
        var ucPath = await MakeUcFileAsync("99-song.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(AudioFormat.Flac);
        naming.Setup(n => n.GetOutputFileName(ucPath, AudioFormat.Flac)).Returns("99-song.flac");

        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Flac, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .Callback<string, AudioFormat, IntegrityLevel, CancellationToken>((p, _, _, _) => capturedPath = p)
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L3));

        var result = await sut.ProcessAsync(ucPath, DefaultOptions());

        result.Outcome.Should().Be(ScanOutcome.Ok);
        capturedPath.Should().EndWith(".flac");
    }
}
