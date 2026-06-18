using System.Collections.Concurrent;
using FluentAssertions;
using FreeBird.Core.Decoding;
using FreeBird.Core.Models;
using FreeBird.Core.Naming;
using FreeBird.Core.Processing;
using FreeBird.Core.Sidecar;
using FreeBird.Core.Watch;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace FreeBird.Core.Tests.Watch;

/// <summary>
/// T06 (v3.0.1) — comprehensive Branch 3b (per-source-stem JSON marker)
/// scenario coverage for <see cref="FilesystemSkipDecider"/>, including the
/// headline K2 multi-source regression that proves the original user-reported
/// bug ("16 .uc! files re-processed every poll") is fixed.
///
/// Each test exercises ONE Branch 3b decision path with a real marker round-tripped
/// through <see cref="ResolutionMarkerSerializer"/> — no JSON hand-crafting except
/// where we intentionally bypass the serializer to seed corrupt input (test #13).
/// </summary>
public sealed class FilesystemSkipDecider_MarkerTests : IDisposable
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

    // ---------- fixture helpers ----------

    private string NewTempDir()
    {
        var root = Path.Combine(Path.GetTempPath(), "fb-marker-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "in"));
        Directory.CreateDirectory(Path.Combine(root, "out"));
        _tempDirs.Add(root);
        return root;
    }

    private static string CreateSource(string root, string fileName, byte[]? bytes = null)
    {
        var path = Path.Combine(root, "in", fileName);
        File.WriteAllBytes(path, bytes ?? new byte[] { 1, 2, 3 });
        return path;
    }

    private static WatchOptions OptionsFor(string root)
    {
        // MinFileSizeBytes = 1 so our tiny 3-byte sources clear the floor.
        return new WatchOptions(
            InputDirs: new[] { Path.Combine(root, "in") },
            OutputDir: Path.Combine(root, "out"),
            MinFileSizeBytes: 1);
    }

    /// <summary>
    /// Build a (decider, sink, serializer) trio that all share the same logger so
    /// the sink captures every log line emitted by both the decider AND the
    /// serializer's TryRead/WriteAtomic paths.
    /// </summary>
    private static (FilesystemSkipDecider decider, InMemorySink sink, ResolutionMarkerSerializer serializer) MakeSut()
    {
        var sink = new InMemorySink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var serializer = new ResolutionMarkerSerializer(logger);
        var decider = new FilesystemSkipDecider(new TextSidecarReader(), serializer, logger, TimeProvider.System);
        return (decider, sink, serializer);
    }

    /// <summary>
    /// Build a fresh Resolved marker that exactly matches the given source's
    /// (size, mtime) and the supplied naming template. Callers override fields
    /// as needed (status, RetryAfter, schema, etc.) via record-with syntax.
    /// </summary>
    private static ResolutionMarker FreshResolvedMarker(
        string sourcePath,
        string outputName,
        string namingTemplate,
        FileInfo? infoOverride = null)
    {
        var info = infoOverride ?? new FileInfo(sourcePath);
        return new ResolutionMarker
        {
            Schema = 1,
            SourceStem = StemBasedFileNamer.GetStem(sourcePath),
            MusicId = MusicIdExtractor.TryExtractAsString(sourcePath) ?? string.Empty,
            SourcePath = sourcePath,
            SourceSize = info.Length,
            SourceMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            ResolvedAt = DateTimeOffset.UtcNow,
            Status = MarkerStatus.Resolved,
            OutputName = outputName,
            Format = "Flac",
            Integrity = "L3",
            NamingTemplate = namingTemplate,
            Reason = null,
            RetryAfter = null,
        };
    }

    private static List<LogEvent> MarkerHitDebug(InMemorySink sink) =>
        sink.Events
            .Where(e => e.Level == LogEventLevel.Debug)
            .Where(e => e.RenderMessage().Contains("marker hit", StringComparison.Ordinal))
            .ToList();

    private static List<LogEvent> InfoEvents(InMemorySink sink) =>
        sink.Events.Where(e => e.Level == LogEventLevel.Information).ToList();

    private static List<LogEvent> WarnEvents(InMemorySink sink) =>
        sink.Events.Where(e => e.Level == LogEventLevel.Warning).ToList();

    // =======================================================================
    // 1. MarkerPresent_SourceUnchanged_StatusResolved_OutputExists_ReturnsSkip_NoApiCall
    // =======================================================================
    [Fact]
    public async Task MarkerPresent_SourceUnchanged_StatusResolved_OutputExists_ReturnsSkip_NoApiCall()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "树离 - 终不负.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate);
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
        decision.Detail.Should().Be(ResolutionMarkerSerializer.MarkerPath(opts.OutputDir, marker.SourceStem));
        MarkerHitDebug(sink).Should().HaveCount(1);
        InfoEvents(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 2. MarkerPresent_SourceUnchanged_StatusResolved_OutputDeleted_ReturnsProcess
    // =======================================================================
    [Fact]
    public async Task MarkerPresent_SourceUnchanged_StatusResolved_OutputDeleted_ReturnsProcess()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "树离 - 终不负.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate);
        serializer.WriteAtomic(opts.OutputDir, marker);
        // Intentionally do NOT pre-create the output file — the belt-and-suspenders
        // check should fire and emit "marker references missing output" at INF.

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeTrue();
        InfoEvents(sink).Should().ContainSingle(e =>
            e.RenderMessage().Contains("marker references missing output", StringComparison.Ordinal));
        MarkerHitDebug(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 3. MarkerPresent_SourceSizeChanged_ReturnsProcess_LogsSourceChanged
    // =======================================================================
    [Fact]
    public async Task MarkerPresent_SourceSizeChanged_ReturnsProcess_LogsSourceChanged()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);
        var info = new FileInfo(src);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "树离 - 终不负.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate, info)
            with
        { SourceSize = info.Length + 1 };
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeTrue();
        InfoEvents(sink).Should().ContainSingle(e =>
            e.RenderMessage().Contains("source changed", StringComparison.Ordinal));
        MarkerHitDebug(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 4. MarkerPresent_SourceMtimeChanged_ReturnsProcess_LogsSourceChanged
    // =======================================================================
    [Fact]
    public async Task MarkerPresent_SourceMtimeChanged_ReturnsProcess_LogsSourceChanged()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);
        var info = new FileInfo(src);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "树离 - 终不负.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate, info)
            with
        { SourceMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).AddSeconds(-5) };
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeTrue();
        InfoEvents(sink).Should().ContainSingle(e =>
            e.RenderMessage().Contains("source changed", StringComparison.Ordinal));
        MarkerHitDebug(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 5. MarkerPresent_NamingTemplateChanged_ReturnsProcess_LogsOldAndNewTemplate
    // =======================================================================
    [Fact]
    public async Task MarkerPresent_NamingTemplateChanged_ReturnsProcess_LogsOldAndNewTemplate()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root) with { NamingTemplate = "{artist} - {title}" };

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "树离 - 终不负.flac";
        // Marker was written with a DIFFERENT template than current options.
        var marker = FreshResolvedMarker(src, outputName, namingTemplate: "{title} - {artist}");
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeTrue();
        var infoLine = InfoEvents(sink).Should().ContainSingle(e =>
            e.RenderMessage().Contains("naming template changed", StringComparison.Ordinal)).Subject;
        var rendered = infoLine.RenderMessage();
        rendered.Should().Contain("{title} - {artist}");
        rendered.Should().Contain("{artist} - {title}");
        MarkerHitDebug(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 6. MarkerPresent_MetadataEmpty_BeforeRetryAfter_ReturnsSkip
    // =======================================================================
    [Fact]
    public async Task MarkerPresent_MetadataEmpty_BeforeRetryAfter_ReturnsSkip()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "3367798042.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate)
            with
        {
            Status = MarkerStatus.MetadataEmpty,
            Reason = "metadata-empty",
            RetryAfter = DateTimeOffset.Now.AddDays(7),
        };
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
        MarkerHitDebug(sink).Should().HaveCount(1);
        InfoEvents(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 7. MarkerPresent_MetadataEmpty_AfterRetryAfter_ReturnsProcess_LogsRetryElapsed
    // =======================================================================
    [Fact]
    public async Task MarkerPresent_MetadataEmpty_AfterRetryAfter_ReturnsProcess_LogsRetryElapsed()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "3367798042.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate)
            with
        {
            Status = MarkerStatus.MetadataEmpty,
            Reason = "metadata-empty",
            RetryAfter = DateTimeOffset.Now.AddMinutes(-1),
        };
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeTrue();
        var infoLine = InfoEvents(sink).Should().ContainSingle(e =>
            e.RenderMessage().Contains("retry-after elapsed", StringComparison.Ordinal)).Subject;
        infoLine.RenderMessage().Should().Contain("MetadataEmpty");
        MarkerHitDebug(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 8. MarkerPresent_MetadataFetchFailed_30MinAfter_ReturnsSkip
    //
    // Status = MetadataFetchFailed (retry table: 1h). RetryAfter set 30 min in
    // the future → still within retry window → Skip.
    // =======================================================================
    [Fact]
    public async Task MarkerPresent_MetadataFetchFailed_30MinAfter_ReturnsSkip()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "3367798042.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate)
            with
        {
            Status = MarkerStatus.MetadataFetchFailed,
            Reason = "metadata-fetch-failed",
            RetryAfter = DateTimeOffset.Now.AddMinutes(30),
        };
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
        MarkerHitDebug(sink).Should().HaveCount(1);
        InfoEvents(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 9. MarkerPresent_MetadataFetchFailed_90MinAfter_ReturnsProcess
    //
    // 90 min elapsed since ResolvedAt where the 1h retry window has passed.
    // We model this directly: RetryAfter set 30 min in the PAST → elapsed → Process.
    // =======================================================================
    [Fact]
    public async Task MarkerPresent_MetadataFetchFailed_90MinAfter_ReturnsProcess()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "3367798042.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate)
            with
        {
            Status = MarkerStatus.MetadataFetchFailed,
            Reason = "metadata-fetch-failed",
            RetryAfter = DateTimeOffset.Now.AddMinutes(-30),
        };
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeTrue();
        var infoLine = InfoEvents(sink).Should().ContainSingle(e =>
            e.RenderMessage().Contains("retry-after elapsed", StringComparison.Ordinal)).Subject;
        infoLine.RenderMessage().Should().Contain("MetadataFetchFailed");
        MarkerHitDebug(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 10. MarkerPresent_MetadataDeserializeFailed_12HoursAfter_ReturnsSkip
    //
    // Status = MetadataDeserializeFailed (retry table: 24h). RetryAfter set 12 h
    // in the future (i.e. 12 h elapsed of a 24 h window) → still within → Skip.
    // =======================================================================
    [Fact]
    public async Task MarkerPresent_MetadataDeserializeFailed_12HoursAfter_ReturnsSkip()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "3367798042.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate)
            with
        {
            Status = MarkerStatus.MetadataDeserializeFailed,
            Reason = "metadata-deserialize-failed",
            RetryAfter = DateTimeOffset.Now.AddHours(12),
        };
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
        MarkerHitDebug(sink).Should().HaveCount(1);
        InfoEvents(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 11. MarkerMissing_MusicIdOnlyFileExists_OfflineFalse_ReturnsProcess_DebugLog_NoInfo
    //
    // No marker present; musicId-only output exists in online mode → Branch 3c
    // online path returns Process and logs "Skip-edge bootstrap" at DBG. No INF.
    // =======================================================================
    [Fact]
    public async Task MarkerMissing_MusicIdOnlyFileExists_OfflineFalse_ReturnsProcess_DebugLog_NoInfo()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, _) = MakeSut();
        File.WriteAllBytes(Path.Combine(opts.OutputDir, "3367798042.flac"), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeTrue();
        sink.Events.Should().ContainSingle(e =>
            e.Level == LogEventLevel.Debug &&
            e.RenderMessage().Contains("Skip-edge bootstrap", StringComparison.Ordinal));
        InfoEvents(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 12. MarkerMissing_MusicIdOnlyFileExists_OfflineTrue_ReturnsSkip
    // =======================================================================
    [Fact]
    public async Task MarkerMissing_MusicIdOnlyFileExists_OfflineTrue_ReturnsSkip()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root) with { Offline = true };

        var (decider, _, _) = MakeSut();
        var musicIdPath = Path.Combine(opts.OutputDir, "3367798042.flac");
        File.WriteAllBytes(musicIdPath, new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecoded);
        decision.Detail.Should().Contain("3367798042.flac");
    }

    // =======================================================================
    // 13. MarkerCorruptJson_FallsThroughToBranch3c_LogsWarn
    //
    // We bypass the serializer (which would reject invalid input) and write
    // corrupt JSON directly. TryRead catches the JsonException → logs WRN ONCE,
    // returns false → decider falls through. With no musicId-only output, the
    // final decision is Process.
    // =======================================================================
    [Fact]
    public async Task MarkerCorruptJson_FallsThroughToBranch3c_LogsWarn()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, _) = MakeSut();
        var stem = StemBasedFileNamer.GetStem(src);
        var markerPath = ResolutionMarkerSerializer.MarkerPath(opts.OutputDir, stem);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        File.WriteAllText(markerPath, "{ this isn't valid }");

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeTrue();
        // Exactly one WRN — from the serializer's TryRead. The decider does NOT
        // emit a second WRN per the "do NOT log a second WRN" comment in T05.
        WarnEvents(sink).Should().ContainSingle(e =>
            e.RenderMessage().Contains("Failed to parse resolution marker", StringComparison.Ordinal));
        WarnEvents(sink).Should().HaveCount(1);
        MarkerHitDebug(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 14. SkipDecider_Schema2Marker_Accepted
    //
    // T2 (Schema 2): a Schema=2 marker is now within the supported range
    // (SupportedMarkerSchema == 2). A fresh, output-present Schema=2 marker must
    // be HONORED (short-circuit → Skip), NOT rejected as unsupported. Replaces
    // the prior MarkerSchemaTwo_FallsThroughToBranch3c_LogsWarn assertion, which
    // pinned the old supported range of 1.
    // =======================================================================
    [Fact]
    public async Task SkipDecider_Schema2Marker_Accepted()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "树离 - 终不负.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate) with { Schema = 2 };
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
        // No "unsupported schema" WRN — schema 2 is now accepted.
        WarnEvents(sink).Should().NotContain(e =>
            e.RenderMessage().Contains("unsupported schema", StringComparison.OrdinalIgnoreCase));
        MarkerHitDebug(sink).Should().ContainSingle();
    }

    // =======================================================================
    // 15. K2_TwoSourcesSameMusicIdDifferentStems_BothShortCircuit_ZeroApiCalls
    //
    // ⭐ Headline regression: two .uc! sources sharing the SAME musicId but
    // with DIFFERENT stems (e.g. lossless 5999 bitrate vs lossy 4444 bitrate)
    // must each short-circuit on their OWN marker. The decider must NOT
    // ping-pong; each Detail must point to its own marker path.
    // =======================================================================
    [Fact]
    public async Task K2_TwoSourcesSameMusicIdDifferentStems_BothShortCircuit_ZeroApiCalls()
    {
        var root = NewTempDir();
        var sourceA = CreateSource(root, "3367798042-_-_5999-_-_aaa.uc!");
        var sourceB = CreateSource(root, "3367798042-_-_4444-_-_bbb.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, serializer) = MakeSut();
        const string outputA = "Lossless.flac";
        const string outputB = "Lossy.flac";

        var markerA = FreshResolvedMarker(sourceA, outputA, opts.NamingTemplate);
        var markerB = FreshResolvedMarker(sourceB, outputB, opts.NamingTemplate);
        serializer.WriteAtomic(opts.OutputDir, markerA);
        serializer.WriteAtomic(opts.OutputDir, markerB);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputA), new byte[] { 0xFF });
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputB), new byte[] { 0xFF });

        var decisionA = await decider.DecideAsync(sourceA, opts, CancellationToken.None);
        var decisionB = await decider.DecideAsync(sourceB, opts, CancellationToken.None);

        decisionA.ShouldProcess.Should().BeFalse();
        decisionB.ShouldProcess.Should().BeFalse();
        decisionA.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
        decisionB.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);

        // CRITICAL: each Detail points to its OWN marker, not the other.
        decisionA.Detail.Should().Be(
            ResolutionMarkerSerializer.MarkerPath(opts.OutputDir, StemBasedFileNamer.GetStem(sourceA)));
        decisionB.Detail.Should().Be(
            ResolutionMarkerSerializer.MarkerPath(opts.OutputDir, StemBasedFileNamer.GetStem(sourceB)));
        // CRITICAL: stems actually differ (sanity — proves the K2 split).
        decisionA.Detail.Should().NotBe(decisionB.Detail);

        // Exactly two DBG "marker hit" lines, zero INF. Proves no ping-pong.
        MarkerHitDebug(sink).Should().HaveCount(2);
        InfoEvents(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 16. SourceWithNoExtractableMusicId_StillShortCircuitsViaMarker
    //
    // Stem has no leading digit run → MusicId is empty. The marker still
    // short-circuits because Branch 3b is keyed on the stem, not the musicId.
    // =======================================================================
    [Fact]
    public async Task SourceWithNoExtractableMusicId_StillShortCircuitsViaMarker()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "random-uuid-no-digits.uc!");
        var opts = OptionsFor(root);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "Artist - Title.flac";
        var marker = FreshResolvedMarker(src, outputName, opts.NamingTemplate);
        // Sanity: stem has no leading digits, so MusicId is empty.
        marker.MusicId.Should().BeEmpty();
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeFalse();
        decision.Reason.Should().Be(SkipReason.AlreadyDecodedViaMarker);
        MarkerHitDebug(sink).Should().HaveCount(1);
        InfoEvents(sink).Should().BeEmpty();
    }

    // =======================================================================
    // 17. CompoundStaleness_SizeAndTemplateBothChanged_ReturnsProcess_LogsSourceChanged
    //
    // Both size AND template are stale. Freshness is checked FIRST, so we
    // must see exactly ONE "source changed" INF and NO template INF.
    // =======================================================================
    [Fact]
    public async Task CompoundStaleness_SizeAndTemplateBothChanged_ReturnsProcess_LogsSourceChanged()
    {
        var root = NewTempDir();
        var src = CreateSource(root, "3367798042-_-_5999-_-_abc.uc!");
        var opts = OptionsFor(root) with { NamingTemplate = "{artist} - {title}" };
        var info = new FileInfo(src);

        var (decider, sink, serializer) = MakeSut();
        const string outputName = "树离 - 终不负.flac";
        var marker = FreshResolvedMarker(src, outputName, namingTemplate: "{title}", infoOverride: info)
            with
        { SourceSize = info.Length + 1 };
        serializer.WriteAtomic(opts.OutputDir, marker);
        File.WriteAllBytes(Path.Combine(opts.OutputDir, outputName), new byte[] { 0xFF });

        var decision = await decider.DecideAsync(src, opts, CancellationToken.None);

        decision.ShouldProcess.Should().BeTrue();
        InfoEvents(sink).Should().ContainSingle(e =>
            e.RenderMessage().Contains("source changed", StringComparison.Ordinal));
        // The template-mismatch INF must NOT fire — freshness check is first
        // and returns early.
        InfoEvents(sink).Should().NotContain(e =>
            e.RenderMessage().Contains("naming template changed", StringComparison.Ordinal));
        MarkerHitDebug(sink).Should().BeEmpty();
    }

    /// <summary>Tiny in-memory Serilog sink for assertions on log output.</summary>
    private sealed class InMemorySink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();
        public IReadOnlyList<LogEvent> Events => _events.ToArray();
        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
