using System.Globalization;
using System.Text;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;
using FreeBird.Core.Sidecar;
using FreeBird.Core.Watch;
using Moq;
using Serilog;

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

    private static FilesystemSkipDecider WithRealReader()
        => new(new TextSidecarReader(), NullLogger());

    private static FilesystemSkipDecider WithMockReader(Mock<ISidecarReader> mock)
        => new(mock.Object, NullLogger());

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
        ((Action)(() => _ = new FilesystemSkipDecider(null!, NullLogger())))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        ((Action)(() => _ = new FilesystemSkipDecider(new TextSidecarReader(), null!)))
            .Should().Throw<ArgumentNullException>();
    }
}
