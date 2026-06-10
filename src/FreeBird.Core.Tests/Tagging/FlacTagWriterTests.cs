using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Tagging;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Tagging;

public sealed class FlacTagWriterTests
{
    // ---- helpers ----------------------------------------------------------

    private static ILogger NullLogger()
    {
        // Mock<ILogger> returns null for ForContext<T>() by default, which then NREs
        // when production code chains a level call on it. Setting it to return the
        // mock itself keeps the call chain alive without writing actual log output.
        var m = new Mock<ILogger>();
        m.Setup(l => l.ForContext<It.IsAnyType>()).Returns(m.Object);
        return m.Object;
    }

    private static FlacTagWriter Build(Mock<IProcessRunner> runner)
        => new(runner.Object, NullLogger());

    private static Mock<IProcessRunner> RunnerReturning(int exit, string stdout = "", string stderr = "")
    {
        var m = new Mock<IProcessRunner>();
        m.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
         .ReturnsAsync(new ProcessResult(exit, stdout, stderr));
        return m;
    }

    // ---- arg-shape tests --------------------------------------------------

    [Fact]
    public void BuildArgs_SingleArtist_ProducesExpectedSequence()
    {
        var song = new SongInfo(1, "Title", new[] { "Artist" }, Album: "Album");

        var args = FlacTagWriter.BuildArgs("/tmp/x.flac", song);

        args.Should().Equal(
            "--remove-all-tags",
            "--set-tag=ARTIST=Artist",
            "--set-tag=TITLE=Title",
            "--set-tag=ALBUM=Album",
            "/tmp/x.flac");
    }

    [Fact]
    public void BuildArgs_MultiArtist_JoinedWith_Slash()
    {
        var song = new SongInfo(1, "T", new[] { "A", "B" });

        var args = FlacTagWriter.BuildArgs("/tmp/x.flac", song);

        args.Should().Contain("--set-tag=ARTIST=A/B");
    }

    [Fact]
    public void BuildArgs_NullAlbum_OmitsAlbumTag()
    {
        var song = new SongInfo(1, "T", new[] { "A" });   // Album defaults to null

        var args = FlacTagWriter.BuildArgs("/tmp/x.flac", song);

        args.Should().NotContain(a => a.StartsWith("--set-tag=ALBUM=", StringComparison.Ordinal));
        args.Should().EndWith("/tmp/x.flac");
    }

    [Fact]
    public void BuildArgs_EmptyAlbum_OmitsAlbumTag()
    {
        var song = new SongInfo(1, "T", new[] { "A" }, Album: "");

        var args = FlacTagWriter.BuildArgs("/tmp/x.flac", song);

        args.Should().NotContain(a => a.StartsWith("--set-tag=ALBUM=", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildArgs_PreservesSpacesInFilenamePath()
    {
        var song = new SongInfo(1, "T", new[] { "A" });

        var args = FlacTagWriter.BuildArgs("/tmp/with space.flac", song);

        // Path is passed verbatim; IProcessRunner is responsible for quoting/escaping
        // (it uses ArgumentList which avoids shell interpretation entirely).
        args[^1].Should().Be("/tmp/with space.flac");
    }

    [Fact]
    public void BuildArgs_CjkContent_PassedThroughUnchanged()
    {
        var song = new SongInfo(1, "终不负", new[] { "树离suliii_" }, Album: "终不负");

        var args = FlacTagWriter.BuildArgs("/tmp/x.flac", song);

        args.Should().Contain("--set-tag=ARTIST=树离suliii_");
        args.Should().Contain("--set-tag=TITLE=终不负");
        args.Should().Contain("--set-tag=ALBUM=终不负");
    }

    // ---- subprocess outcome tests -----------------------------------------

    [Fact]
    public async Task WriteAsync_ZeroExit_ReturnsSuccess()
    {
        var runner = RunnerReturning(0);
        var sut = Build(runner);

        var result = await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Success>();
    }

    [Fact]
    public async Task WriteAsync_NonZeroExit_ReturnsFailed_TagWriteFailed()
    {
        var runner = RunnerReturning(1, stderr: "metaflac: invalid FLAC");
        var sut = Build(runner);

        var result = await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("tag-write-failed");
    }

    [Fact]
    public async Task WriteAsync_RunnerThrowsWin32Exception_ReturnsFailed_TagToolMissing()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new System.ComponentModel.Win32Exception("No such file"));
        var sut = Build(runner);

        var result = await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("tag-tool-missing");
    }

    [Fact]
    public async Task WriteAsync_RunnerThrowsOther_ReturnsFailed_TagIoError()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("boom"));
        var sut = Build(runner);

        var result = await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("tag-io-error");
    }

    [Fact]
    public async Task WriteAsync_CallsMetaflac_AsFileName_AndPropagatesArgs()
    {
        IReadOnlyList<string>? capturedArgs = null;
        string? capturedExe = null;
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .Callback<string, IReadOnlyList<string>, CancellationToken>((exe, args, _) =>
              {
                  capturedExe = exe;
                  capturedArgs = args;
              })
              .ReturnsAsync(new ProcessResult(0, "", ""));
        var sut = Build(runner);

        await sut.WriteAsync("/tmp/x.flac", new SongInfo(42, "T", new[] { "A" }), CancellationToken.None);

        capturedExe.Should().Be("metaflac");
        capturedArgs.Should().NotBeNull();
        capturedArgs!.Should().StartWith(new[] { "--remove-all-tags" });
        capturedArgs.Should().EndWith(new[] { "/tmp/x.flac" });
    }

    [Fact]
    public async Task WriteAsync_CancellationPropagates()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new OperationCanceledException());
        var sut = Build(runner);

        await ((Func<Task>)(async () =>
            await sut.WriteAsync("/tmp/x.flac", new SongInfo(1, "t", new[] { "a" }), CancellationToken.None)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- constructor guards ----------------------------------------------

    [Fact]
    public void Constructor_NullRunner_Throws()
        => ((Action)(() => _ = new FlacTagWriter(null!, new Mock<ILogger>().Object)))
            .Should().Throw<ArgumentNullException>();

    [Fact]
    public void Constructor_NullLogger_Throws()
        => ((Action)(() => _ = new FlacTagWriter(new Mock<IProcessRunner>().Object, null!)))
            .Should().Throw<ArgumentNullException>();
}
