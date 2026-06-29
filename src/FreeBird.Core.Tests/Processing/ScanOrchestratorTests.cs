using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using FreeBird.Core.Processing;
using FreeBird.Core.Tests.TestSupport;
using Moq;
using Serilog;
using Serilog.Core;

namespace FreeBird.Core.Tests.Processing;

public class ScanOrchestratorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-orch-{Guid.NewGuid():N}");
    private readonly string _inputDir;
    private readonly string _outputDir;
    private readonly Logger _testLogger;

    public ScanOrchestratorTests()
    {
        _inputDir = Path.Combine(_tempDir, "in");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDir);
        Directory.CreateDirectory(_outputDir);
        // Silent logger — default config writes to nothing
        _testLogger = new LoggerConfiguration().CreateLogger();
    }

    public void Dispose()
    {
        _testLogger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private ScanOptions DefaultOptions(int concurrency = 2) =>
        new(new[] { _inputDir }, _outputDir, IntegrityLevel.Auto, concurrency, CollisionPolicy.Skip);

    private async Task<string> TouchAsync(string name)
    {
        var path = Path.Combine(_inputDir, name);
        await File.WriteAllBytesAsync(path, new byte[] { 0xEA });
        return path;
    }

    // Permissive gate: always returns null (fall-through => process), preserving
    // pre-T3 ScanOrchestrator behavior for every existing test.
    private static Mock<IResolvedMarkerGate> PassthroughGate()
    {
        var gate = new Mock<IResolvedMarkerGate>();
        gate.Setup(g => g.TryShortCircuit(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((SkipDecision?)null);
        return gate;
    }

    private ScanOrchestrator NewSut(IFileProcessor processor, ILogger logger) =>
        new(SingleProcessorRouter.For(processor), logger, PassthroughGate().Object);

    // --- Discovery ---

    [Fact]
    public async Task RunAsync_NoFiles_ReturnsZeroSummary()
    {
        var proc = new Mock<IFileProcessor>(MockBehavior.Strict);
        var sut = NewSut(proc.Object, _testLogger);

        var summary = await sut.RunAsync(DefaultOptions());

        summary.Processed.Should().Be(0);
        summary.Ok.Should().Be(0);
        summary.HasFailures.Should().BeFalse();
        proc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_OnlyUcFiles_DispatchesEach()
    {
        await TouchAsync("a.uc");
        await TouchAsync("b.uc!");
        await TouchAsync("c.txt");      // filtered out
        await TouchAsync("d.UC.txt");   // filtered out

        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, ScanOptions _, CancellationToken _) =>
                new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3));

        var sut = NewSut(proc.Object, _testLogger);
        var summary = await sut.RunAsync(DefaultOptions(concurrency: 1));

        summary.Processed.Should().Be(2);
        summary.Ok.Should().Be(2);
        proc.Verify(p => p.ProcessAsync(It.Is<string>(s => s.EndsWith("a.uc")), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        proc.Verify(p => p.ProcessAsync(It.Is<string>(s => s.EndsWith("b.uc!")), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_NcmFile_Discovered_AndRoutedForProcessing()
    {
        // Task 15: .ncm files must be discovered by EnumerateSources and dispatched
        // through the router (which would select NcmFileProcessor in production).
        await TouchAsync("song.ncm");

        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, ScanOptions _, CancellationToken _) =>
                new ScanResult(path, ScanOutcome.Ok, AudioFormat.Flac));

        // A router that records which path it was asked to route — proves the
        // orchestrator consulted the router for the discovered .ncm file.
        var router = new Mock<IFileProcessorRouter>();
        router.Setup(r => r.Select(It.IsAny<string>())).Returns(proc.Object);

        var sut = new ScanOrchestrator(router.Object, _testLogger, PassthroughGate().Object);
        var summary = await sut.RunAsync(DefaultOptions(concurrency: 1));

        summary.Processed.Should().Be(1);
        summary.Ok.Should().Be(1);
        router.Verify(r => r.Select(It.Is<string>(s => s.EndsWith("song.ncm"))), Times.Once);
        proc.Verify(p => p.ProcessAsync(It.Is<string>(s => s.EndsWith("song.ncm")), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_HiddenFile_Excluded()
    {
        await TouchAsync(".hidden.uc");
        await TouchAsync("visible.uc");

        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, ScanOptions _, CancellationToken _) =>
                new ScanResult(path, ScanOutcome.Ok));

        var sut = NewSut(proc.Object, _testLogger);
        var summary = await sut.RunAsync(DefaultOptions());

        summary.Processed.Should().Be(1);
        proc.Verify(p => p.ProcessAsync(It.Is<string>(s => s.EndsWith(".hidden.uc")), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_NonRecursive_NestedDirIgnored()
    {
        await TouchAsync("top.uc");
        var nested = Path.Combine(_inputDir, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllBytesAsync(Path.Combine(nested, "deep.uc"), new byte[] { 1 });

        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, ScanOptions _, CancellationToken _) =>
                new ScanResult(path, ScanOutcome.Ok));

        var sut = NewSut(proc.Object, _testLogger);
        var summary = await sut.RunAsync(DefaultOptions());

        summary.Processed.Should().Be(1);
        proc.Verify(p => p.ProcessAsync(It.Is<string>(s => s.EndsWith("top.uc")), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Aggregation ---

    [Fact]
    public async Task RunAsync_MixedOutcomes_SummaryCountsCorrect()
    {
        var pOk = await TouchAsync("ok.uc");
        var pSk = await TouchAsync("skip.uc");
        var pUn = await TouchAsync("unk.uc");
        var pIf = await TouchAsync("intfail.uc");
        var pEr = await TouchAsync("err.uc");

        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(pOk, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ScanResult(pOk, ScanOutcome.Ok));
        proc.Setup(p => p.ProcessAsync(pSk, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ScanResult(pSk, ScanOutcome.Skipped));
        proc.Setup(p => p.ProcessAsync(pUn, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ScanResult(pUn, ScanOutcome.UnknownFormat));
        proc.Setup(p => p.ProcessAsync(pIf, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ScanResult(pIf, ScanOutcome.IntegrityFailed));
        proc.Setup(p => p.ProcessAsync(pEr, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ScanResult(pEr, ScanOutcome.Error));

        var sut = NewSut(proc.Object, _testLogger);
        var summary = await sut.RunAsync(DefaultOptions());

        summary.Processed.Should().Be(5);
        summary.Ok.Should().Be(1);
        summary.Skipped.Should().Be(1);
        summary.UnknownFormat.Should().Be(1);
        summary.IntegrityFailed.Should().Be(1);
        summary.Errors.Should().Be(1);
        summary.HasFailures.Should().BeTrue();
        summary.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task RunAsync_ProcessorThrows_CountedAsError()
    {
        await TouchAsync("a.uc");
        await TouchAsync("b.uc");

        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var sut = NewSut(proc.Object, _testLogger);
        var summary = await sut.RunAsync(DefaultOptions());

        summary.Processed.Should().Be(2);
        summary.Errors.Should().Be(2);
        summary.Ok.Should().Be(0);
    }

    // --- Cancellation ---

    [Fact]
    public async Task RunAsync_Cancelled_PropagatesOCE()
    {
        await TouchAsync("a.uc");
        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, ScanOptions _, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                return new ScanResult("x", ScanOutcome.Ok);
            });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sut = NewSut(proc.Object, _testLogger);
        Func<Task> act = () => sut.RunAsync(DefaultOptions(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // --- Concurrency ---

    [Fact]
    public async Task RunAsync_ConcurrencyLimit_ObservedAsCapOnInflight()
    {
        for (int i = 0; i < 8; i++) { await TouchAsync($"f{i}.uc"); }

        int inflight = 0, maxInflight = 0;
        var gate = new object();
        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (string path, ScanOptions _, CancellationToken _) =>
            {
                lock (gate) { inflight++; maxInflight = Math.Max(maxInflight, inflight); }
                await Task.Delay(30);
                lock (gate) { inflight--; }
                return new ScanResult(path, ScanOutcome.Ok);
            });

        var sut = NewSut(proc.Object, _testLogger);
        await sut.RunAsync(DefaultOptions(concurrency: 3));

        maxInflight.Should().BeLessThanOrEqualTo(3);
        maxInflight.Should().BeGreaterThan(1, "parallelism should actually have occurred with concurrency=3 and 8 files");
    }

    // --- Validation ---

    [Fact]
    public async Task RunAsync_NullOptions_Throws()
    {
        var sut = NewSut(new Mock<IFileProcessor>().Object, _testLogger);
        Func<Task> act = () => sut.RunAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RunAsync_NonexistentInputDir_Throws()
    {
        var sut = NewSut(new Mock<IFileProcessor>().Object, _testLogger);
        var bad = new ScanOptions(new[] { "/nonexistent/dir/xyz" }, _outputDir, IntegrityLevel.Auto, 1, CollisionPolicy.Skip);
        Func<Task> act = () => sut.RunAsync(bad);
        await act.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public void Constructor_NullRouter_Throws()
    {
        Action act = () => _ = new ScanOrchestrator(null!, _testLogger, PassthroughGate().Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Action act = () => _ = new ScanOrchestrator(new Mock<IFileProcessorRouter>().Object, null!, PassthroughGate().Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullResolvedMarkerGate_Throws()
    {
        Action act = () => _ = new ScanOrchestrator(new Mock<IFileProcessorRouter>().Object, _testLogger, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // --- v3.4 T13: multi-input semantics ---

    [Fact]
    public async Task RunAsync_MultipleInputDirs_ProcessesAll()
    {
        // Two input dirs, each with one .uc file. The orchestrator must enumerate
        // BOTH and dispatch processing for each file.
        var inputA = Path.Combine(_tempDir, "in-a");
        var inputB = Path.Combine(_tempDir, "in-b");
        Directory.CreateDirectory(inputA);
        Directory.CreateDirectory(inputB);
        var fileA = Path.Combine(inputA, "a.uc");
        var fileB = Path.Combine(inputB, "b.uc!");
        await File.WriteAllBytesAsync(fileA, new byte[] { 0xEA });
        await File.WriteAllBytesAsync(fileB, new byte[] { 0xEA });

        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, ScanOptions _, CancellationToken _) =>
                new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3));

        var opts = new ScanOptions(new[] { inputA, inputB }, _outputDir, IntegrityLevel.Auto, 1, CollisionPolicy.Skip);
        var sut = NewSut(proc.Object, _testLogger);
        var summary = await sut.RunAsync(opts);

        summary.Processed.Should().Be(2);
        summary.Ok.Should().Be(2);
        proc.Verify(p => p.ProcessAsync(fileA, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        proc.Verify(p => p.ProcessAsync(fileB, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_OneInputDirMissing_ThrowsDirectoryNotFound_BeforeAnyProcessing()
    {
        // Fail-fast: the first missing directory aborts the run BEFORE any files
        // are dispatched. Mock is strict so any ProcessAsync call would throw.
        await TouchAsync("present.uc"); // file in _inputDir (which exists)

        var proc = new Mock<IFileProcessor>(MockBehavior.Strict);
        var opts = new ScanOptions(
            new[] { _inputDir, "/nonexistent/dir/zzz" },
            _outputDir,
            IntegrityLevel.Auto, 1, CollisionPolicy.Skip);

        var sut = NewSut(proc.Object, _testLogger);
        Func<Task> act = () => sut.RunAsync(opts);

        await act.Should().ThrowAsync<DirectoryNotFoundException>()
                 .WithMessage("*nonexistent/dir/zzz*");
        // Strict mock proves no ProcessAsync was invoked.
        proc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_EmptyInputDirsList_ReturnsZeroSummary()
    {
        var proc = new Mock<IFileProcessor>(MockBehavior.Strict);
        var opts = new ScanOptions(Array.Empty<string>(), _outputDir, IntegrityLevel.Auto, 1, CollisionPolicy.Skip);

        var sut = NewSut(proc.Object, _testLogger);
        var summary = await sut.RunAsync(opts);

        summary.Processed.Should().Be(0);
        summary.Ok.Should().Be(0);
        summary.HasFailures.Should().BeFalse();
        proc.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RunAsync_AllInputDirsMissing_ThrowsOnFirstMissing()
    {
        var proc = new Mock<IFileProcessor>(MockBehavior.Strict);
        var opts = new ScanOptions(
            new[] { "/nope/a", "/nope/b" },
            _outputDir,
            IntegrityLevel.Auto, 1, CollisionPolicy.Skip);

        var sut = NewSut(proc.Object, _testLogger);
        Func<Task> act = () => sut.RunAsync(opts);

        // Fail-fast on the first missing dir, not the last.
        await act.Should().ThrowAsync<DirectoryNotFoundException>()
                 .WithMessage("*/nope/a*");
    }

    [Fact]
    public async Task RunAsync_FilesAcrossMultipleInputs_AggregatedIntoOneSummary()
    {
        // Two input dirs each contribute files of different outcomes. Summary
        // counters must aggregate the union (not be per-dir).
        var inputA = Path.Combine(_tempDir, "in-agg-a");
        var inputB = Path.Combine(_tempDir, "in-agg-b");
        Directory.CreateDirectory(inputA);
        Directory.CreateDirectory(inputB);
        var aOk = Path.Combine(inputA, "1.uc");
        var aSk = Path.Combine(inputA, "2.uc");
        var bOk = Path.Combine(inputB, "3.uc");
        var bEr = Path.Combine(inputB, "4.uc");
        foreach (var f in new[] { aOk, aSk, bOk, bEr })
        {
            await File.WriteAllBytesAsync(f, new byte[] { 0xEA });
        }

        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(aOk, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult(aOk, ScanOutcome.Ok, AudioFormat.Mp3));
        proc.Setup(p => p.ProcessAsync(aSk, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult(aSk, ScanOutcome.Skipped, AudioFormat.Mp3));
        proc.Setup(p => p.ProcessAsync(bOk, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult(bOk, ScanOutcome.Ok, AudioFormat.Mp3));
        proc.Setup(p => p.ProcessAsync(bEr, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScanResult(bEr, ScanOutcome.Error, AudioFormat.Mp3));

        var opts = new ScanOptions(new[] { inputA, inputB }, _outputDir, IntegrityLevel.Auto, 1, CollisionPolicy.Skip);
        var sut = NewSut(proc.Object, _testLogger);
        var summary = await sut.RunAsync(opts);

        summary.Processed.Should().Be(4);
        summary.Ok.Should().Be(2);
        summary.Skipped.Should().Be(1);
        summary.Errors.Should().Be(1);
    }

    // --- T3: marker gate short-circuit (THE CORE FIX) ---

    [Fact]
    public async Task RunAsync_GateReturnsSkip_DoesNotCallProcessAsync()
    {
        // Regression: an already-resolved file must be skipped BEFORE any
        // IFileProcessor.ProcessAsync call (which is where the NetEase metadata
        // API would be hit). Gate returns a Skip => zero ProcessAsync calls.
        var file = await TouchAsync("resolved.uc");

        var gate = new Mock<IResolvedMarkerGate>();
        gate.Setup(g => g.TryShortCircuit(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(SkipDecision.Skip(SkipReason.AlreadyDecodedViaMarker, "marker"));

        var proc = new Mock<IFileProcessor>(MockBehavior.Strict);

        var sut = new ScanOrchestrator(SingleProcessorRouter.For(proc.Object), _testLogger, gate.Object);
        var summary = await sut.RunAsync(DefaultOptions(concurrency: 1));

        proc.Verify(p => p.ProcessAsync(
            It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        summary.Processed.Should().Be(1);
        summary.Skipped.Should().Be(1);
        summary.Ok.Should().Be(0);
        gate.Verify(g => g.TryShortCircuit(
            file, _outputDir, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_GateReturnsNull_CallsProcessAsyncOnce()
    {
        // Fall-through: gate returns null => the file is processed exactly as
        // before T3 (Outcome flows into the summary).
        var file = await TouchAsync("fresh.uc");

        var gate = new Mock<IResolvedMarkerGate>();
        gate.Setup(g => g.TryShortCircuit(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((SkipDecision?)null);

        var proc = new Mock<IFileProcessor>();
        proc.Setup(p => p.ProcessAsync(It.IsAny<string>(), It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, ScanOptions _, CancellationToken _) =>
                new ScanResult(path, ScanOutcome.Ok, AudioFormat.Mp3));

        var sut = new ScanOrchestrator(SingleProcessorRouter.For(proc.Object), _testLogger, gate.Object);
        var summary = await sut.RunAsync(DefaultOptions(concurrency: 1));

        proc.Verify(p => p.ProcessAsync(
            file, It.IsAny<ScanOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        summary.Processed.Should().Be(1);
        summary.Ok.Should().Be(1);
        summary.Skipped.Should().Be(0);
    }
}
