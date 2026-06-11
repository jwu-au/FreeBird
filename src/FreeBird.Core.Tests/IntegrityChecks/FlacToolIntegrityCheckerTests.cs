using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Infrastructure;
using FreeBird.Core.Integrity;
using FreeBird.Core.Models;
using FreeBird.Core.Provisioning;
using Moq;
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
        _sut = new FlacToolIntegrityChecker(new SystemProcessRunner(), PathResolver("flac"));
    }

    // Resolver that returns whatever path the system would resolve `flac` to via PATH.
    // For integration tests using the real SystemProcessRunner, returning the literal
    // "flac" lets the OS handle PATH lookup — preserving prior behavior.
    private static IFlacBinaryResolver PathResolver(string binaryName)
    {
        var m = new Mock<IFlacBinaryResolver>();
        m.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(new FlacResolution(binaryName, FlacBinaryProvenance.Path));
        m.Setup(r => r.ResolveMetaflacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(new FlacResolution(binaryName, FlacBinaryProvenance.Path));
        return m.Object;
    }

    private static IFlacBinaryResolver NotFoundResolver()
    {
        var m = new Mock<IFlacBinaryResolver>();
        m.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(FlacResolution.NotFound);
        m.Setup(r => r.ResolveMetaflacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(FlacResolution.NotFound);
        return m.Object;
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
        Action act = () => _ = new FlacToolIntegrityChecker(null!, PathResolver("flac"));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullResolver_Throws()
    {
        Action act = () => _ = new FlacToolIntegrityChecker(new SystemProcessRunner(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CheckAsync_ResolverNotFound_ThrowsFlacNotAvailableException()
    {
        // Real file must exist so we get past the FileNotFoundException guard;
        // any FLAC file works since the resolver gate fires before the subprocess.
        var sut = new FlacToolIntegrityChecker(new SystemProcessRunner(), NotFoundResolver());

        Func<Task> act = () => sut.CheckAsync(Fx.SampleFlacPath, AudioFormat.Flac);

        await act.Should().ThrowAsync<FlacNotAvailableException>();
    }

    [Fact]
    public async Task CheckAsync_PassesResolvedPath_NotHardcodedFlacString()
    {
        // Verify the process runner receives the resolver-supplied path, not the
        // hardcoded literal "flac". This is the core T12 contract.
        const string ResolvedPath = "/custom/path/to/flac";
        string? capturedExe = null;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .Callback<string, IReadOnlyList<string>, CancellationToken>((exe, _, _) => capturedExe = exe)
              .ReturnsAsync(new ProcessResult(0, "", ""));
        var resolver = new Mock<IFlacBinaryResolver>();
        resolver.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new FlacResolution(ResolvedPath, FlacBinaryProvenance.CliOverride));
        var sut = new FlacToolIntegrityChecker(runner.Object, resolver.Object);

        await sut.CheckAsync(Fx.SampleFlacPath, AudioFormat.Flac);

        capturedExe.Should().Be(ResolvedPath);
    }
}
