using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Integrity;
using FreeBird.Core.Models;
using Fx = FreeBird.Core.Tests.Fixtures.Fixtures;

namespace FreeBird.Core.Tests.IntegrityChecks;

public class TagLibIntegrityCheckerTests : IDisposable
{
    private readonly TagLibIntegrityChecker _sut = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-l1-{Guid.NewGuid():N}");

    public TagLibIntegrityCheckerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CheckAsync_ValidMp3Fixture_ReturnsPassed()
    {
        var result = await _sut.CheckAsync(Fx.SampleMp3Path, AudioFormat.Mp3);
        result.Ok.Should().BeTrue();
        result.LevelApplied.Should().Be(IntegrityLevel.L1);
        result.Reason.Should().BeNull();
    }

    [Fact]
    public async Task CheckAsync_ValidFlacFixture_ReturnsPassed()
    {
        var result = await _sut.CheckAsync(Fx.SampleFlacPath, AudioFormat.Flac);
        result.Ok.Should().BeTrue();
        result.LevelApplied.Should().Be(IntegrityLevel.L1);
    }

    [Fact]
    public async Task CheckAsync_GarbageFile_ReturnsFailed()
    {
        var garbage = Path.Combine(_tempDir, "garbage.mp3");
        await File.WriteAllBytesAsync(garbage, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55 });

        var result = await _sut.CheckAsync(garbage, AudioFormat.Mp3);
        result.Ok.Should().BeFalse();
        result.LevelApplied.Should().Be(IntegrityLevel.L1);
        result.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CheckAsync_EmptyFile_ReturnsFailed()
    {
        var empty = Path.Combine(_tempDir, "empty.mp3");
        await File.WriteAllBytesAsync(empty, Array.Empty<byte>());

        var result = await _sut.CheckAsync(empty, AudioFormat.Mp3);
        result.Ok.Should().BeFalse();
        result.LevelApplied.Should().Be(IntegrityLevel.L1);
        result.Reason.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CheckAsync_TruncatedFlac_ReturnsFailed()
    {
        // Truncate to just the FLAC magic bytes — enough to be recognized but no audio data
        var truncated = Path.Combine(_tempDir, "truncated.flac");
        await File.WriteAllBytesAsync(truncated, new byte[] { 0x66, 0x4C, 0x61, 0x43 }); // "fLaC"

        var result = await _sut.CheckAsync(truncated, AudioFormat.Flac);
        result.Ok.Should().BeFalse();
        result.LevelApplied.Should().Be(IntegrityLevel.L1);
    }

    [Fact]
    public async Task CheckAsync_NonexistentFile_Throws()
    {
        Func<Task> act = () => _sut.CheckAsync("/nonexistent/file.mp3", AudioFormat.Mp3);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task CheckAsync_NullPath_Throws()
    {
        Func<Task> act = () => _sut.CheckAsync(null!, AudioFormat.Mp3);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CheckAsync_FormatParameterIgnored_PassedRegardless()
    {
        // L1 doesn't care about format — just runs TagLib on whatever is at the path
        var result1 = await _sut.CheckAsync(Fx.SampleMp3Path, AudioFormat.Flac); // "wrong" format
        var result2 = await _sut.CheckAsync(Fx.SampleMp3Path, AudioFormat.Unknown);
        result1.Ok.Should().BeTrue();
        result2.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task CheckAsync_CancellationBeforeWork_RespectsToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => _sut.CheckAsync(Fx.SampleMp3Path, AudioFormat.Mp3, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
