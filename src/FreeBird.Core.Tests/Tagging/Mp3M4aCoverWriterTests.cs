using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Tagging;
using Moq;
using Serilog;
using Fx = FreeBird.Core.Tests.Fixtures.Fixtures;

namespace FreeBird.Core.Tests.Tagging;

/// <summary>
/// Integration tests for <see cref="Mp3M4aCoverWriter"/> using real TagLibSharp +
/// the committed <c>sample.mp3</c> fixture. Mirrors <see cref="Mp3TagWriterTests"/>.
/// </summary>
public sealed class Mp3M4aCoverWriterTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"fb-mp3-cover-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static ILogger NullLogger()
    {
        var m = new Mock<ILogger>();
        m.Setup(l => l.ForContext<It.IsAnyType>()).Returns(m.Object);
        return m.Object;
    }

    private static Mp3M4aCoverWriter Build() => new(NullLogger());

    private string CopyFixture(string name = "track.mp3")
    {
        Skip.IfNot(File.Exists(Fx.SampleMp3Path), $"MP3 fixture missing at {Fx.SampleMp3Path}");
        Directory.CreateDirectory(_tempDir);
        var dst = Path.Combine(_tempDir, name);
        File.Copy(Fx.SampleMp3Path, dst, overwrite: true);
        return dst;
    }

    // A minimal but valid-enough JPEG/PNG magic prefix; TagLib stores the bytes
    // verbatim as picture data, it does not decode the image.
    private static byte[] JpegBytes() => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00 };
    private static byte[] PngBytes() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00 };

    // ---- unit tests (always run) -----------------------------------------

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Action act = () => new Mp3M4aCoverWriter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteCoverAsync_NullPath_Throws()
    {
        var w = Build();
        Func<Task> act = () => w.WriteCoverAsync(null!, JpegBytes(), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WriteCoverAsync_NullCover_Throws()
    {
        var w = Build();
        Func<Task> act = () => w.WriteCoverAsync("/tmp/x.mp3", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteCoverAsync_CancelledToken_Throws()
    {
        var w = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> act = () => w.WriteCoverAsync("/tmp/x.mp3", JpegBytes(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WriteCoverAsync_NonexistentFile_ReturnsFailed()
    {
        var w = Build();
        var bogus = Path.Combine(_tempDir, "nope.mp3");
        Directory.CreateDirectory(_tempDir);

        var result = await w.WriteCoverAsync(bogus, JpegBytes(), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>();
    }

    // ---- MIME detection helper unit tests --------------------------------

    [Fact]
    public void DetectMime_Jpeg_FromMagic()
    {
        CoverMime.Detect(JpegBytes()).Should().Be("image/jpeg");
    }

    [Fact]
    public void DetectMime_Png_FromMagic()
    {
        CoverMime.Detect(PngBytes()).Should().Be("image/png");
    }

    [Fact]
    public void DetectMime_Unknown_DefaultsToJpeg()
    {
        CoverMime.Detect(new byte[] { 0x00, 0x01, 0x02, 0x03 }).Should().Be("image/jpeg");
    }

    // ---- integration tests (fixture-gated) -------------------------------

    [SkippableFact]
    public async Task Mp3_EmbedsJpegCover_WithFrontCoverType()
    {
        var path = CopyFixture("jpeg.mp3");
        var writer = Build();

        var result = await writer.WriteCoverAsync(path, JpegBytes(), CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);

        using var f = TagLib.File.Create(path);
        f.Tag.Pictures.Should().HaveCount(1);
        f.Tag.Pictures[0].MimeType.Should().Be("image/jpeg");
        f.Tag.Pictures[0].Type.Should().Be(TagLib.PictureType.FrontCover);
    }

    [SkippableFact]
    public async Task Mp3_EmbedsPngCover_DetectsPngMime()
    {
        var path = CopyFixture("png.mp3");
        var writer = Build();

        var result = await writer.WriteCoverAsync(path, PngBytes(), CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);

        using var f = TagLib.File.Create(path);
        f.Tag.Pictures.Should().HaveCount(1);
        f.Tag.Pictures[0].MimeType.Should().Be("image/png");
    }

    [SkippableFact]
    public async Task Mp3_WritingCoverTwice_YieldsSinglePicture()
    {
        var path = CopyFixture("twice.mp3");
        var writer = Build();

        (await writer.WriteCoverAsync(path, JpegBytes(), CancellationToken.None))
            .Should().Be(TagWriteResult.Success.Instance);
        (await writer.WriteCoverAsync(path, JpegBytes(), CancellationToken.None))
            .Should().Be(TagWriteResult.Success.Instance);

        using var f = TagLib.File.Create(path);
        f.Tag.Pictures.Should().HaveCount(1, "cover write replaces the picture set, never appends");
    }
}
