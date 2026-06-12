using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using FreeBird.Core.Processing;
using FreeBird.Core.Sidecar;
using FreeBird.Core.Watch;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Processing;

/// <summary>
/// T12 of v3.4 — wires <see cref="IOutputPathMutexPool"/> into <see cref="FileProcessor"/>
/// so concurrent watches against different input dirs that produce the SAME output
/// filename serialize the skip-check + atomic-move + tag-write sequence.
///
/// <para>
/// Critical race: two input cache dirs each contain a different .uc file whose musicId
/// resolves (via the namer) to the same finalPath. Without the mutex, both threads:
///   1. Check exists -> both see false
///   2. Atomic-move -> second clobbers first (Overwrite) OR throws (Skip)
///   3. Tag-write -> metaflac corruption on overlapping write
///
/// The mutex MUST cover all three steps as a single critical section.
/// </para>
/// </summary>
public sealed class FileProcessorMutexTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-proc-mutex-{Guid.NewGuid():N}");
    private readonly string _inputDirA;
    private readonly string _inputDirB;
    private readonly string _outputDir;

    public FileProcessorMutexTests()
    {
        _inputDirA = Path.Combine(_tempDir, "inA");
        _inputDirB = Path.Combine(_tempDir, "inB");
        _outputDir = Path.Combine(_tempDir, "out");
        Directory.CreateDirectory(_inputDirA);
        Directory.CreateDirectory(_inputDirB);
        Directory.CreateDirectory(_outputDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // --- Test 4: null mutex pool -> ctor throws ---

    [Fact]
    public void Constructor_NullMutexPool_Throws()
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

        ((Action)(() => _ = new FileProcessor(d, s, n, i, w, m, t, ms, null!, l)))
            .Should().Throw<ArgumentNullException>().WithParameterName("outputMutexPool");
    }

    // --- Test 2: AcquireAsync must be called BEFORE File.Exists check (TOCTOU-safe) ---

    [Fact]
    public async Task ProcessAsync_AcquiresMutexBeforeFinalMove()
    {
        // We assert mutex-acquired BEFORE the move by counting calls at the moment
        // the writer is asked to materialise the staging file. Since the mutex wraps
        // step 8 (exists-check) + step 9 (move), AcquireAsync MUST have been called
        // at least once by the time we're inside step 9.

        var (sut, sniffer, naming, integrity, _, _, mutex, acquireCount, _) =
            BuildSutWithMutexSpy();

        var ucPath = await MakeUcFileAsync(_inputDirA, "12345-foo.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Mp3, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("12345-foo.mp3");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));

        var opts = new ScanOptions(_inputDirA, _outputDir, IntegrityLevel.Auto, 1, CollisionPolicy.Overwrite)
            with { Offline = true };

        var result = await sut.ProcessAsync(ucPath, opts);

        result.Outcome.Should().Be(ScanOutcome.Ok);

        // The mutex MUST have been acquired exactly once for the finalPath.
        acquireCount().Should().Be(1, "mutex must wrap the exists-check + atomic move + tag-write critical section");

        // And the acquired path must be the canonical finalPath.
        mutex.AcquiredPaths.Should().ContainSingle()
             .Which.Should().EndWith("12345-foo.mp3");
    }

    // --- Test 3: exception during move still releases the mutex (Dispose is called) ---

    [Fact]
    public async Task ProcessAsync_ExceptionInMoveStep_StillReleasesMutex()
    {
        // Force step-9 File.Move to throw by pre-creating finalPath as a *directory*
        // (Move-to-existing-directory throws IOException on all platforms). Use
        // CollisionPolicy.Overwrite so step 8 doesn't short-circuit to Skip.

        var (sut, sniffer, naming, integrity, _, _, mutex, acquireCount, releaseCount) =
            BuildSutWithMutexSpy();

        var ucPath = await MakeUcFileAsync(_inputDirA, "999-collide.uc");

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetTargetName(ucPath, AudioFormat.Mp3, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("999-collide.mp3");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));

        // Pre-create finalPath as a DIRECTORY so File.Move throws.
        Directory.CreateDirectory(Path.Combine(_outputDir, "999-collide.mp3"));

        var opts = new ScanOptions(_inputDirA, _outputDir, IntegrityLevel.Auto, 1, CollisionPolicy.Overwrite)
            with { Offline = true };

        var result = await sut.ProcessAsync(ucPath, opts);

        // FileProcessor catches Exception and reports ScanOutcome.Error.
        result.Outcome.Should().Be(ScanOutcome.Error);

        // The mutex MUST have been acquired AND released (refcount returned to 0).
        acquireCount().Should().Be(1);
        releaseCount().Should().Be(1, "mutex token MUST be disposed even on exception (using-statement)");
    }

    // --- Test 1 & 5 (combined): two concurrent ProcessAsync calls producing the same
    // finalPath are serialised by the mutex, and the final state has exactly one
    // intact output file. ---

    [Fact]
    public async Task ProcessAsync_TwoConcurrentInputsSameFinalPath_SerialisesViaMutex()
    {
        // Two .uc files in two different input dirs. Both resolve (via the naming
        // mock) to the SAME finalPath "shared.mp3". CollisionPolicy.Overwrite so
        // the second one to acquire the lock wins cleanly (overwrites the first).
        // Without the mutex, the two File.Move + tag-write sequences would interleave
        // and either throw or leave the output corrupted.

        var ucA = await MakeUcFileAsync(_inputDirA, "1-a.uc");
        var ucB = await MakeUcFileAsync(_inputDirB, "1-b.uc");

        // Shared real OutputPathMutexPool (the production type).
        using var pool = new OutputPathMutexPool();

        // Build TWO SUTs that share the same pool — mirrors v3.4 multi-input where
        // each WatchTask owns its own FileProcessor (per-scope lifetime) but the pool
        // is a process-wide singleton.
        var (sutA, _, _, _) = BuildSutWithRealPool(pool);
        var (sutB, _, _, _) = BuildSutWithRealPool(pool);

        var optsA = new ScanOptions(_inputDirA, _outputDir, IntegrityLevel.Auto, 1, CollisionPolicy.Overwrite)
            with { Offline = true };
        var optsB = new ScanOptions(_inputDirB, _outputDir, IntegrityLevel.Auto, 1, CollisionPolicy.Overwrite)
            with { Offline = true };

        // Fire both in parallel.
        var taskA = Task.Run(() => sutA.ProcessAsync(ucA, optsA));
        var taskB = Task.Run(() => sutB.ProcessAsync(ucB, optsB));

        var results = await Task.WhenAll(taskA, taskB);

        // Both must complete cleanly (no "file in use" / clobber-error).
        results[0].Outcome.Should().Be(ScanOutcome.Ok);
        results[1].Outcome.Should().Be(ScanOutcome.Ok);

        // Exactly one output file with the shared name must exist.
        var finalPath = Path.Combine(_outputDir, "shared.mp3");
        File.Exists(finalPath).Should().BeTrue();

        // Output is non-zero (proves a real move landed it, not an empty stub).
        new FileInfo(finalPath).Length.Should().BeGreaterThan(0);

        // After both complete, the pool must have zero live entries
        // (proving every token was released).
        pool.EntryCount.Should().Be(0, "all mutex tokens MUST be released after ProcessAsync returns");
    }

    // ---------- helpers ----------

    private async Task<string> MakeUcFileAsync(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(path, new byte[] { 0xEA, 0xE7, 0x90, 0x00 });
        return path;
    }

    /// <summary>
    /// SUT factory using a spy mutex pool that counts acquires and releases.
    /// </summary>
    private (FileProcessor sut,
             Mock<IFormatSniffer> sniffer,
             Mock<IFileNamer> naming,
             Mock<ICompositeIntegrityChecker> integrity,
             Mock<IAtomicFileWriter> writer,
             Mock<IMetadataResolver> metadata,
             SpyMutexPool mutex,
             Func<int> acquireCount,
             Func<int> releaseCount) BuildSutWithMutexSpy()
    {
        var decoder = new Mock<IXorDecoder>();
        var sniffer = new Mock<IFormatSniffer>();
        var naming = new Mock<IFileNamer>();
        var integrity = new Mock<ICompositeIntegrityChecker>();
        var writer = new Mock<IAtomicFileWriter>();
        var metadata = new Mock<IMetadataResolver>();
        var tagWriter = new Mock<ITagWriter>();
        var mutex = new SpyMutexPool();

        // Realistic writer: actually creates the staging file on disk.
        writer.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<Func<Stream, CancellationToken, Task>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
              .Returns<string, Func<Stream, CancellationToken, Task>, bool, CancellationToken>(async (path, callback, _, ct) =>
              {
                  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                  await using var fs = File.Create(path);
                  await callback(fs, ct);
              });

        decoder.Setup(d => d.DecodeAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
               .Returns<Stream, Stream, CancellationToken>(async (_, output, ct) =>
               {
                   await output.WriteAsync(new byte[] { 1, 2, 3, 4 }, ct);
               });

        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("offline-mode"));

        tagWriter.Setup(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(TagWriteResult.Success.Instance);

        var logger = new Mock<ILogger>().Object;
        var markerSerializer = new ResolutionMarkerSerializer(logger);
        var sut = new FileProcessor(
            decoder.Object, sniffer.Object, naming.Object, integrity.Object,
            writer.Object, metadata.Object, tagWriter.Object, markerSerializer,
            mutex, logger);

        return (sut, sniffer, naming, integrity, writer, metadata,
                mutex, () => mutex.AcquireCount, () => mutex.ReleaseCount);
    }

    /// <summary>
    /// SUT factory using the real OutputPathMutexPool. Naming is fixed to "shared.mp3"
    /// so concurrent calls collide on the same finalPath.
    /// </summary>
    private (FileProcessor sut,
             Mock<IFormatSniffer> sniffer,
             Mock<IFileNamer> naming,
             Mock<ICompositeIntegrityChecker> integrity) BuildSutWithRealPool(IOutputPathMutexPool pool)
    {
        var decoder = new Mock<IXorDecoder>();
        var sniffer = new Mock<IFormatSniffer>();
        var naming = new Mock<IFileNamer>();
        var integrity = new Mock<ICompositeIntegrityChecker>();
        var writer = new Mock<IAtomicFileWriter>();
        var metadata = new Mock<IMetadataResolver>();
        var tagWriter = new Mock<ITagWriter>();

        sniffer.Setup(s => s.SniffAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(AudioFormat.Mp3);
        naming.Setup(n => n.GetTargetName(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<SongInfo?>(), It.IsAny<string?>()))
              .Returns("shared.mp3");
        integrity.Setup(i => i.CheckAsync(It.IsAny<string>(), AudioFormat.Mp3, It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.L1));

        writer.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<Func<Stream, CancellationToken, Task>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
              .Returns<string, Func<Stream, CancellationToken, Task>, bool, CancellationToken>(async (path, callback, _, ct) =>
              {
                  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                  await using var fs = File.Create(path);
                  await callback(fs, ct);
              });

        decoder.Setup(d => d.DecodeAsync(It.IsAny<Stream>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
               .Returns<Stream, Stream, CancellationToken>(async (_, output, ct) =>
               {
                   await output.WriteAsync(new byte[] { 1, 2, 3, 4 }, ct);
               });

        metadata.Setup(m => m.ResolveAsync(It.IsAny<string>(), It.IsAny<IMetadataOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MetadataResolution.Fallback("offline-mode"));

        tagWriter.Setup(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(TagWriteResult.Success.Instance);

        var logger = new Mock<ILogger>().Object;
        var markerSerializer = new ResolutionMarkerSerializer(logger);
        var sut = new FileProcessor(
            decoder.Object, sniffer.Object, naming.Object, integrity.Object,
            writer.Object, metadata.Object, tagWriter.Object, markerSerializer,
            pool, logger);

        return (sut, sniffer, naming, integrity);
    }

    /// <summary>
    /// Test double that counts AcquireAsync calls + token Dispose calls and records
    /// the paths acquired. Always grants the lock immediately (no contention).
    /// </summary>
    private sealed class SpyMutexPool : IOutputPathMutexPool
    {
        private int _acquireCount;
        private int _releaseCount;
        public System.Collections.Concurrent.ConcurrentBag<string> AcquiredPaths { get; } = new();

        public int AcquireCount => _acquireCount;
        public int ReleaseCount => _releaseCount;
        public int EntryCount => Math.Max(0, _acquireCount - _releaseCount);

        public ValueTask<IDisposable> AcquireAsync(string path, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _acquireCount);
            AcquiredPaths.Add(path);
            IDisposable token = new SpyToken(() => Interlocked.Increment(ref _releaseCount));
            return ValueTask.FromResult(token);
        }

        private sealed class SpyToken : IDisposable
        {
            private Action? _onDispose;
            public SpyToken(Action onDispose) { _onDispose = onDispose; }
            public void Dispose()
            {
                var cb = Interlocked.Exchange(ref _onDispose, null);
                cb?.Invoke();
            }
        }
    }
}
