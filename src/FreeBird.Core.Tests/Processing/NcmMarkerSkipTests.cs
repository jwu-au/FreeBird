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
using FreeBird.Core.Tests.Ncm;
using FreeBird.Core.Watch;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;
using Xunit;

namespace FreeBird.Core.Tests.Processing;

/// <summary>
/// Task 16 (MANDATORY) — the one design assumption (Q5) that MUST be verified
/// empirically: a successful <c>.ncm</c> decode writes a Resolution marker whose
/// <see cref="ResolutionMarker.MusicId"/> is the empty string (the field is
/// <c>required string</c> and cannot be null, and there is no NetEase lookup for
/// <c>.ncm</c>). The skip path on BOTH scan and watch is
/// <see cref="ResolvedMarkerGate.TryShortCircuit"/> (scan calls it directly;
/// watch reaches it via <c>FilesystemSkipDecider</c> Branch 3b).
///
/// This test proves END-TO-END that the empty-MusicId marker is honored: after the
/// REAL <see cref="NcmFileProcessor"/> produces the decoded output + marker, the REAL
/// <see cref="ResolvedMarkerGate"/> (wired with the SAME serializer instance) returns
/// a NON-NULL skip decision for the source. As a non-vacuous guard it also asserts
/// that BEFORE the marker exists the gate returns null (would NOT skip).
/// </summary>
public sealed class NcmMarkerSkipTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-ncm-skip-{Guid.NewGuid():N}");
    private readonly string _inputDir;
    private readonly string _outputDir;

    public NcmMarkerSkipTests()
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
        new(MusicId: 555, MusicName: "Marker Song", Album: "Marker Album",
            Artists: new[] { ("Gate Tester", 7L) }, Format: "flac");

    [Fact]
    public async Task EmptyMusicIdMarker_FromNcmDecode_CausesGateToSkip_ButNotBeforeMarkerExists()
    {
        // --- Arrange: a flac-bodied .ncm in the input dir. ---
        var body = await File.ReadAllBytesAsync(Fixtures.Fixtures.SampleFlacPath);
        var ncmBytes = NcmTestEncoder.Encode(body, FlacMeta(), cover: null);
        var sourcePath = Path.Combine(_inputDir, "song.ncm");
        await File.WriteAllBytesAsync(sourcePath, ncmBytes);

        // Real collaborators, EXCEPT integrity (faked Ok) and tag/cover (no-op spies)
        // which would otherwise shell out to flac/metaflac. The marker serializer and
        // the gate share the SAME instance so the marker WRITTEN is the marker READ.
        var logger = new Mock<ILogger>().Object;
        var markerSerializer = new ResolutionMarkerSerializer(logger);
        var timeProvider = new FakeTimeProvider();

        var integrity = new Mock<ICompositeIntegrityChecker>();
        integrity
            .Setup(i => i.CheckAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<IntegrityLevel>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IntegrityResult.Passed(IntegrityLevel.Off));

        var tagWriter = new Mock<ITagWriter>();
        tagWriter
            .Setup(t => t.WriteAsync(It.IsAny<string>(), It.IsAny<AudioFormat>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);
        var coverWriter = new Mock<ICoverWriter>();

        var processor = new NcmFileProcessor(
            new NcmDecoder(),
            new MagicByteFormatSniffer(),
            integrity.Object,
            new AtomicFileWriter(),
            tagWriter.Object,
            coverWriter.Object,
            markerSerializer,
            new OutputPathMutexPool(),
            logger,
            timeProvider);

        var options = new ScanOptions(new[] { _inputDir }, _outputDir, IntegrityLevel.Off, 1, CollisionPolicy.Skip);

        // Build the REAL gate wired with the SAME serializer + a fresh clock.
        var gate = new ResolvedMarkerGate(markerSerializer, logger, new FakeTimeProvider());

        // --- Non-vacuous guard: BEFORE any processing there is no marker → no skip. ---
        var beforeDecision = gate.TryShortCircuit(sourcePath, _outputDir, options.NamingTemplate);
        beforeDecision.Should().BeNull("no marker exists yet, so the gate must NOT short-circuit");

        // --- Act: run the real .ncm pipeline. Produces decoded output + Resolved marker. ---
        var result = await processor.ProcessAsync(sourcePath, options);
        result.Outcome.Should().Be(ScanOutcome.Ok);

        // Prove the marker that landed actually has MusicId == "" (the Q5 premise).
        var markerPath = ResolutionMarkerSerializer.MarkerPath(_outputDir, "song");
        markerSerializer.TryRead(markerPath, out var marker).Should().BeTrue();
        marker!.MusicId.Should().Be("", "the .ncm success path writes an empty (non-null) MusicId");
        marker.Status.Should().Be(MarkerStatus.Resolved);

        // --- Assert: the empty-MusicId marker NOW causes the gate to SKIP. ---
        var afterDecision = gate.TryShortCircuit(sourcePath, _outputDir, options.NamingTemplate);
        afterDecision.Should().NotBeNull(
            "the marker keys on stem+size+mtime+template+output-existence and NEVER reads MusicId, " +
            "so an empty-MusicId Resolved marker must short-circuit the next cycle");
        afterDecision!.ShouldProcess.Should().BeFalse();
        afterDecision.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
    }
}
