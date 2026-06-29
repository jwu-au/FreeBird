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

/// <summary>
/// Dispatch tests for <see cref="CompositeCoverWriter"/>. Mirrors
/// <see cref="CompositeTagWriterTests"/>: backends are mocked, we only assert the
/// composite routes by <see cref="AudioFormat"/> and propagates results / throws
/// for Unknown.
/// </summary>
public sealed class CompositeCoverWriterTests
{
    private static ILogger NullLogger()
    {
        var m = new Mock<ILogger>();
        m.Setup(l => l.ForContext<It.IsAnyType>()).Returns(m.Object);
        return m.Object;
    }

    private sealed record Mocks(
        Mock<IFlacCoverWriter> Flac,
        Mock<IMp3M4aCoverWriter> Taglib,
        CompositeCoverWriter Composite);

    private static Mocks Build()
    {
        var flac = new Mock<IFlacCoverWriter>();
        var taglib = new Mock<IMp3M4aCoverWriter>();
        flac.Setup(w => w.WriteCoverAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);
        taglib.Setup(w => w.WriteCoverAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TagWriteResult.Success.Instance);
        var composite = new CompositeCoverWriter(flac.Object, taglib.Object, NullLogger());
        return new Mocks(flac, taglib, composite);
    }

    private static byte[] Cover() => new byte[] { 0xFF, 0xD8, 0xFF, 0x00 };

    [Fact]
    public async Task Dispatches_FlacWriter_For_FlacFormat()
    {
        var m = Build();

        var result = await m.Composite.WriteCoverAsync("/tmp/x.flac", AudioFormat.Flac, Cover(), CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);
        m.Flac.Verify(w => w.WriteCoverAsync("/tmp/x.flac", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);
        m.Taglib.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatches_TaglibWriter_For_Mp3Format()
    {
        var m = Build();

        var result = await m.Composite.WriteCoverAsync("/tmp/x.mp3", AudioFormat.Mp3, Cover(), CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);
        m.Taglib.Verify(w => w.WriteCoverAsync("/tmp/x.mp3", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);
        m.Flac.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Dispatches_TaglibWriter_For_M4aFormat()
    {
        var m = Build();

        var result = await m.Composite.WriteCoverAsync("/tmp/x.m4a", AudioFormat.M4a, Cover(), CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);
        m.Taglib.Verify(w => w.WriteCoverAsync("/tmp/x.m4a", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);
        m.Flac.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Throws_NotSupportedException_For_UnknownFormat()
    {
        var m = Build();

        Func<Task> act = () => m.Composite.WriteCoverAsync("/tmp/x.dat", AudioFormat.Unknown, Cover(), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
        m.Flac.VerifyNoOtherCalls();
        m.Taglib.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Propagates_Failed_Result_From_Backend()
    {
        var flac = new Mock<IFlacCoverWriter>();
        var taglib = new Mock<IMp3M4aCoverWriter>();
        taglib.Setup(w => w.WriteCoverAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new TagWriteResult.Failed("cover-io-error"));
        var composite = new CompositeCoverWriter(flac.Object, taglib.Object, NullLogger());

        var result = await composite.WriteCoverAsync("/tmp/x.mp3", AudioFormat.Mp3, Cover(), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>()
              .Which.SidecarReason.Should().Be("cover-io-error");
    }

    [Fact]
    public void Constructor_ThrowsOn_NullDependencies()
    {
        var flac = new Mock<IFlacCoverWriter>().Object;
        var taglib = new Mock<IMp3M4aCoverWriter>().Object;
        var log = NullLogger();

        Action a1 = () => new CompositeCoverWriter(null!, taglib, log);
        Action a2 = () => new CompositeCoverWriter(flac, null!, log);
        Action a3 = () => new CompositeCoverWriter(flac, taglib, null!);

        a1.Should().Throw<ArgumentNullException>();
        a2.Should().Throw<ArgumentNullException>();
        a3.Should().Throw<ArgumentNullException>();
    }
}
