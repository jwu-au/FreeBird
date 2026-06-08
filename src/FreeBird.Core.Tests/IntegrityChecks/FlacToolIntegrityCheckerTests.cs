using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Infrastructure;
using FreeBird.Core.Integrity;
using FreeBird.Core.Models;
using Xunit;
using Fx = FreeBird.Core.Tests.Fixtures.Fixtures;

namespace FreeBird.Core.Tests.IntegrityChecks;

public class FlacToolIntegrityCheckerTests : IDisposable
{
    private readonly FlacToolIntegrityChecker _sut;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fb-l3-{Guid.NewGuid():N}");
    private static readonly bool FlacAvailable = ProbeFlacOnce();

    public FlacToolIntegrityCheckerTests()
    {
        Directory.CreateDirectory(_tempDir);
        _sut = new FlacToolIntegrityChecker(new SystemProcessRunner());
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static bool ProbeFlacOnce()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "flac",
                ArgumentList = { "--version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task CheckAsync_ValidFlacFixture_ReturnsPassed()
    {
        Skip.IfNot(FlacAvailable, "flac binary not on PATH");
        var result = await _sut.CheckAsync(Fx.SampleFlacPath, AudioFormat.Flac);
        result.Ok.Should().BeTrue(result.Reason ?? "<no reason>");
        result.LevelApplied.Should().Be(IntegrityLevel.L3);
    }

    [SkippableFact]
    public async Task CheckAsync_CorruptedFlac_ReturnsFailed()
    {
        Skip.IfNot(FlacAvailable, "flac binary not on PATH");

        // Take valid FLAC and mutate a 16-byte run in the middle of the audio area.
        // Mid-file ensures we're past the metadata blocks and corrupt actual frame data,
        // which triggers FRAME_CRC_MISMATCH / decode failure from `flac -t`.
        var corrupted = Path.Combine(_tempDir, "corrupted.flac");
        var bytes = await File.ReadAllBytesAsync(Fx.SampleFlacPath);
        var mid = bytes.Length / 2;
        for (int i = 0; i < 16; i++) { bytes[mid + i] ^= 0xFF; }
        await File.WriteAllBytesAsync(corrupted, bytes);

        var result = await _sut.CheckAsync(corrupted, AudioFormat.Flac);
        result.Ok.Should().BeFalse();
        result.LevelApplied.Should().Be(IntegrityLevel.L3);
        result.Reason.Should().NotBeNullOrEmpty();
    }

    [SkippableFact]
    public async Task CheckAsync_TruncatedFlac_ReturnsFailed()
    {
        Skip.IfNot(FlacAvailable, "flac binary not on PATH");

        var truncated = Path.Combine(_tempDir, "truncated.flac");
        var bytes = await File.ReadAllBytesAsync(Fx.SampleFlacPath);
        await File.WriteAllBytesAsync(truncated, bytes.AsMemory(0, bytes.Length / 2).ToArray());

        var result = await _sut.CheckAsync(truncated, AudioFormat.Flac);
        result.Ok.Should().BeFalse();
        result.LevelApplied.Should().Be(IntegrityLevel.L3);
    }

    [Fact]
    public async Task CheckAsync_NonFlacFormat_ReturnsFailedImmediately_NoSubprocess()
    {
        // Even without flac binary, MP3 input should short-circuit
        var result = await _sut.CheckAsync(Fx.SampleMp3Path, AudioFormat.Mp3);
        result.Ok.Should().BeFalse();
        result.LevelApplied.Should().Be(IntegrityLevel.L3);
        result.Reason.Should().Contain("FLAC");
    }

    [Fact]
    public async Task CheckAsync_NonexistentFile_Throws()
    {
        Func<Task> act = () => _sut.CheckAsync("/nonexistent/file.flac", AudioFormat.Flac);
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task CheckAsync_NullPath_Throws()
    {
        Func<Task> act = () => _sut.CheckAsync(null!, AudioFormat.Flac);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullRunner_Throws()
    {
        Action act = () => _ = new FlacToolIntegrityChecker(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
