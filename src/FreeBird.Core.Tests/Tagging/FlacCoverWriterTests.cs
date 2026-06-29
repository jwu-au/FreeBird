using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Infrastructure;
using FreeBird.Core.Metadata;
using FreeBird.Core.Provisioning;
using FreeBird.Core.Tagging;
using Moq;
using Serilog;
using Fx = FreeBird.Core.Tests.Fixtures.Fixtures;

namespace FreeBird.Core.Tests.Tagging;

/// <summary>
/// Tests for <see cref="FlacCoverWriter"/>. Unit tests use a faked
/// <see cref="IProcessRunner"/> + <see cref="IFlacBinaryResolver"/>; the critical
/// integration test uses the REAL metaflac binary (gated via <see cref="SkippableFact"/>
/// exactly like <see cref="FlacTagWriterTests"/>) to PROVE PCM-MD5 is preserved.
/// </summary>
public sealed class FlacCoverWriterTests
{
    // ---- helpers (mirror FlacTagWriterTests) ------------------------------

    private static ILogger NullLogger()
    {
        var m = new Mock<ILogger>();
        m.Setup(l => l.ForContext<It.IsAnyType>()).Returns(m.Object);
        return m.Object;
    }

    private static IFlacBinaryResolver DefaultResolver(string metaflacPath = "metaflac")
    {
        var m = new Mock<IFlacBinaryResolver>();
        m.Setup(r => r.ResolveMetaflacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(new FlacResolution(metaflacPath, FlacBinaryProvenance.Path));
        m.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(new FlacResolution("flac", FlacBinaryProvenance.Path));
        return m.Object;
    }

    private static IFlacBinaryResolver NotFoundResolver()
    {
        var m = new Mock<IFlacBinaryResolver>();
        m.Setup(r => r.ResolveMetaflacAsync(It.IsAny<CancellationToken>()))
         .ReturnsAsync(FlacResolution.NotFound);
        return m.Object;
    }

    private static FlacCoverWriter Build(Mock<IProcessRunner> runner, IFlacBinaryResolver? resolver = null)
        => new(runner.Object, resolver ?? DefaultResolver(), NullLogger());

    private static Mock<IProcessRunner> RunnerReturning(int exit, string stdout = "", string stderr = "")
    {
        var m = new Mock<IProcessRunner>();
        m.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
         .ReturnsAsync(new ProcessResult(exit, stdout, stderr));
        return m;
    }

    private static byte[] JpegBytes() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

    // ---- unit tests (always run) -----------------------------------------

    [Fact]
    public void Constructor_NullRunner_Throws()
    {
        Action act = () => new FlacCoverWriter(null!, DefaultResolver(), NullLogger());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullResolver_Throws()
    {
        Action act = () => new FlacCoverWriter(RunnerReturning(0).Object, null!, NullLogger());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Action act = () => new FlacCoverWriter(RunnerReturning(0).Object, DefaultResolver(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteCoverAsync_ResolverNotFound_ThrowsFlacNotAvailableException()
    {
        var sut = Build(RunnerReturning(0), NotFoundResolver());

        Func<Task> act = () => sut.WriteCoverAsync("/tmp/x.flac", JpegBytes(), CancellationToken.None);

        await act.Should().ThrowAsync<FlacNotAvailableException>();
    }

    [Fact]
    public async Task WriteCoverAsync_ZeroExit_ReturnsSuccess()
    {
        var sut = Build(RunnerReturning(0));

        var result = await sut.WriteCoverAsync("/tmp/x.flac", JpegBytes(), CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);
    }

    [Fact]
    public async Task WriteCoverAsync_NonZeroExit_ReturnsFailed_CoverWriteFailed()
    {
        var sut = Build(RunnerReturning(1, stderr: "boom"));

        var result = await sut.WriteCoverAsync("/tmp/x.flac", JpegBytes(), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("cover-write-failed");
    }

    [Fact]
    public async Task WriteCoverAsync_RunnerThrowsWin32Exception_ReturnsFailed_CoverToolMissing()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new System.ComponentModel.Win32Exception("not found"));
        var sut = Build(runner);

        var result = await sut.WriteCoverAsync("/tmp/x.flac", JpegBytes(), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("cover-tool-missing");
    }

    [Fact]
    public async Task WriteCoverAsync_RunnerThrowsOther_ReturnsFailed_CoverIoError()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("weird"));
        var sut = Build(runner);

        var result = await sut.WriteCoverAsync("/tmp/x.flac", JpegBytes(), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("cover-io-error");
    }

    [Fact]
    public async Task WriteCoverAsync_UsesResolvedMetaflacPath_NotHardcoded()
    {
        const string ResolvedPath = "/opt/homebrew/bin/metaflac";
        string? capturedExe = null;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .Callback<string, IReadOnlyList<string>, CancellationToken>((exe, _, _) => capturedExe = exe)
              .ReturnsAsync(new ProcessResult(0, "", ""));
        var sut = Build(runner, DefaultResolver(metaflacPath: ResolvedPath));

        await sut.WriteCoverAsync("/tmp/x.flac", JpegBytes(), CancellationToken.None);

        capturedExe.Should().Be(ResolvedPath);
    }

    [Fact]
    public async Task WriteCoverAsync_DeletesTempFile_EvenWhenMetaflacFails()
    {
        // Capture the temp file path the writer hands to metaflac (last spec arg),
        // then assert it no longer exists after a failing run — proving the finally
        // cleanup runs on the failure path too.
        // Two-call flow: the remove call succeeds (exit 0); the import call fails
        // (exit 1). The import call carries the spec arg from which we recover the
        // staged temp path.
        string? tempFromSpec = null;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((string _, IReadOnlyList<string> args, CancellationToken _) =>
              {
                  // Spec form: "--import-picture-from=TYPE|MIME|DESC|DIMS|FILE"
                  var spec = args.FirstOrDefault(a => a.StartsWith("--import-picture-from=", StringComparison.Ordinal));
                  if (spec is not null)
                  {
                      var pipe = spec.LastIndexOf('|');
                      tempFromSpec = spec[(pipe + 1)..];
                      return new ProcessResult(1, "", "fail"); // import fails
                  }
                  return new ProcessResult(0, "", ""); // remove succeeds
              });
        var sut = Build(runner);

        var result = await sut.WriteCoverAsync("/tmp/x.flac", JpegBytes(), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>();
        tempFromSpec.Should().NotBeNull("the writer must stage the image to a temp file");
        File.Exists(tempFromSpec!).Should().BeFalse("the temp file must be deleted in finally even on failure");
    }

    // ---- integration test (real metaflac, gated) -------------------------

    private static readonly bool MetaflacAvailable = ProbeOnce("metaflac");
    private static readonly bool FlacAvailable = ProbeOnce("flac");

    private static bool ProbeOnce(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
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

    private static (int Exit, string Stdout, string Stderr) RunTool(string exe, params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }.WithArgs(args))!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, stdout, stderr);
    }

    private static string ShowMd5(string flacPath)
    {
        var (exit, stdout, stderr) = RunTool("metaflac", "--show-md5sum", flacPath);
        if (exit != 0)
        {
            throw new InvalidOperationException($"metaflac --show-md5sum failed: {stderr}");
        }
        return stdout.Trim();
    }

    private static int CountPictureBlocks(string flacPath)
    {
        var (_, stdout, _) = RunTool("metaflac", "--list", "--block-type=PICTURE", flacPath);
        return stdout.Split('\n').Count(l => l.Contains("type: 6 (PICTURE)", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE critical PCM-MD5 test. Embeds cover art via the real FlacCoverWriter and
    /// asserts: (a) the FLAC streaminfo PCM-MD5 (metaflac --show-md5sum) is unchanged
    /// before vs after — cover art must NOT touch audio frames; (b) flac -t still
    /// reports the file ok; (c) exactly one PICTURE block exists with image/jpeg.
    /// </summary>
    [SkippableFact]
    public async Task WriteCoverAsync_RealMetaflac_PreservesPcmMd5_AndEmbedsPicture()
    {
        Skip.IfNot(MetaflacAvailable, "metaflac binary not on PATH");
        Skip.IfNot(FlacAvailable, "flac binary not on PATH");

        var tempDir = Path.Combine(Path.GetTempPath(), $"fb-cover-md5-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var flacPath = Path.Combine(tempDir, "track.flac");
            File.Copy(Fx.SampleFlacPath, flacPath, overwrite: true);

            var md5Before = ShowMd5(flacPath);
            md5Before.Should().NotBeNullOrWhiteSpace();

            var resolver = new Mock<IFlacBinaryResolver>();
            resolver.Setup(r => r.ResolveMetaflacAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new FlacResolution("metaflac", FlacBinaryProvenance.Path));
            var sut = new FlacCoverWriter(new SystemProcessRunner(), resolver.Object, NullLogger());

            var result = await sut.WriteCoverAsync(flacPath, JpegBytes(), CancellationToken.None);
            result.Should().Be(TagWriteResult.Success.Instance);

            // (a) PCM-MD5 unchanged — the whole point of using metaflac over TagLib.
            ShowMd5(flacPath).Should().Be(md5Before, "embedding cover art must not alter audio frames");

            // (b) flac -t structural + PCM integrity ok.
            RunTool("flac", "-t", flacPath).Exit.Should().Be(0, "flac -t must report the file ok after cover embed");

            // (c) exactly one PICTURE block, image/jpeg.
            CountPictureBlocks(flacPath).Should().Be(1);
            RunTool("metaflac", "--list", "--block-type=PICTURE", flacPath)
                .Stdout.Should().Contain("image/jpeg");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Idempotence: writing a cover twice must yield exactly ONE picture block,
    /// proving the remove-then-import sequence.
    /// </summary>
    [SkippableFact]
    public async Task WriteCoverAsync_RealMetaflac_Idempotent_SinglePictureBlock()
    {
        Skip.IfNot(MetaflacAvailable, "metaflac binary not on PATH");

        var tempDir = Path.Combine(Path.GetTempPath(), $"fb-cover-idem-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var flacPath = Path.Combine(tempDir, "track.flac");
            File.Copy(Fx.SampleFlacPath, flacPath, overwrite: true);

            var resolver = new Mock<IFlacBinaryResolver>();
            resolver.Setup(r => r.ResolveMetaflacAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new FlacResolution("metaflac", FlacBinaryProvenance.Path));
            var sut = new FlacCoverWriter(new SystemProcessRunner(), resolver.Object, NullLogger());

            (await sut.WriteCoverAsync(flacPath, JpegBytes(), CancellationToken.None))
                .Should().Be(TagWriteResult.Success.Instance);
            (await sut.WriteCoverAsync(flacPath, JpegBytes(), CancellationToken.None))
                .Should().Be(TagWriteResult.Success.Instance);

            CountPictureBlocks(flacPath).Should().Be(1, "writing a cover twice must not duplicate the PICTURE block");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
