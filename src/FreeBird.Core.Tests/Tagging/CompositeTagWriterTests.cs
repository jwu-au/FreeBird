using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Models;
using FreeBird.Core.Tagging;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Tagging;

public sealed class CompositeTagWriterTests
{
    private static ILogger NullLogger()
    {
        var m = new Mock<ILogger>();
        m.Setup(l => l.ForContext<It.IsAnyType>()).Returns(m.Object);
        return m.Object;
    }

    private sealed record Mocks(
        Mock<IFlacTagWriter> Flac,
        Mock<IMp3TagWriter> Mp3,
        Mock<IM4aTagWriter> M4a,
        CompositeTagWriter Composite);

    private static Mocks Build()
    {
        var flac = new Mock<IFlacTagWriter>();
        var mp3 = new Mock<IMp3TagWriter>();
        var m4a = new Mock<IM4aTagWriter>();
        flac.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);
        mp3.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);
        m4a.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);
        var composite = new CompositeTagWriter(flac.Object, mp3.Object, m4a.Object, NullLogger());
        return new Mocks(flac, mp3, m4a, composite);
    }

    private static SongInfo Song() => new(123, "Title", new[] { "Artist" });

    [Fact]
    public async Task Dispatches_FlacWriter_For_FlacFormat()
    {
        var m = Build();

        var result = await m.Composite.WriteAsync("/tmp/x.flac", AudioFormat.Flac, Song(), CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);
        m.Flac.Verify(w => w.WriteAsync("/tmp/x.flac", It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()), Times.Once);
        m.Mp3.VerifyNoOtherCalls();
        m.M4a.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatches_Mp3Writer_For_Mp3Format()
    {
        var m = Build();

        var result = await m.Composite.WriteAsync("/tmp/x.mp3", AudioFormat.Mp3, Song(), CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);
        m.Mp3.Verify(w => w.WriteAsync("/tmp/x.mp3", It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()), Times.Once);
        m.Flac.VerifyNoOtherCalls();
        m.M4a.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatches_M4aWriter_For_M4aFormat()
    {
        var m = Build();

        var result = await m.Composite.WriteAsync("/tmp/x.m4a", AudioFormat.M4a, Song(), CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);
        m.M4a.Verify(w => w.WriteAsync("/tmp/x.m4a", It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()), Times.Once);
        m.Flac.VerifyNoOtherCalls();
        m.Mp3.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Throws_NotSupportedException_For_UnknownFormat()
    {
        var m = Build();

        Func<Task> act = () => m.Composite.WriteAsync("/tmp/x.dat", AudioFormat.Unknown, Song(), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
        m.Flac.VerifyNoOtherCalls();
        m.Mp3.VerifyNoOtherCalls();
        m.M4a.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Propagates_Failed_Result_From_Backend()
    {
        var flac = new Mock<IFlacTagWriter>();
        var mp3 = new Mock<IMp3TagWriter>();
        var m4a = new Mock<IM4aTagWriter>();
        mp3.Setup(w => w.WriteAsync(It.IsAny<string>(), It.IsAny<SongInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagWriteResult.Failed("tag-io-error"));
        var composite = new CompositeTagWriter(flac.Object, mp3.Object, m4a.Object, NullLogger());

        var result = await composite.WriteAsync("/tmp/x.mp3", AudioFormat.Mp3, Song(), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("tag-io-error");
    }

    [Fact]
    public void Constructor_ThrowsOn_NullDependencies()
    {
        var flac = new Mock<IFlacTagWriter>().Object;
        var mp3 = new Mock<IMp3TagWriter>().Object;
        var m4a = new Mock<IM4aTagWriter>().Object;
        var log = NullLogger();

        Action a1 = () => new CompositeTagWriter(null!, mp3, m4a, log);
        Action a2 = () => new CompositeTagWriter(flac, null!, m4a, log);
        Action a3 = () => new CompositeTagWriter(flac, mp3, null!, log);
        Action a4 = () => new CompositeTagWriter(flac, mp3, m4a, null!);

        a1.Should().Throw<ArgumentNullException>();
        a2.Should().Throw<ArgumentNullException>();
        a3.Should().Throw<ArgumentNullException>();
        a4.Should().Throw<ArgumentNullException>();
    }
}
