using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Tagging;
using FreeBird.Core.Tests.Fixtures;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Tagging;

/// <summary>
/// Integration tests for <see cref="Mp3TagWriter"/> using real TagLibSharp +
/// the committed <c>sample.mp3</c> fixture. Each test copies the fixture into
/// a fresh temp directory so the original stays clean.
///
/// Tests are gated on <see cref="SkippableFact"/> only for fixture presence \u2014
/// TagLibSharp itself is always available on .NET 10 + 2.3.0 per the v3 spec.
/// </summary>
public sealed class Mp3TagWriterTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"fb-mp3-tag-{Guid.NewGuid():N}");

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

    private static Mp3TagWriter Build() => new(NullLogger());

    private string CopyFixture(string name = "track.mp3")
    {
        Skip.IfNot(File.Exists(Fixtures.Fixtures.SampleMp3Path),
            $"MP3 fixture missing at {Fixtures.Fixtures.SampleMp3Path}");
        Directory.CreateDirectory(_tempDir);
        var dst = Path.Combine(_tempDir, name);
        File.Copy(Fixtures.Fixtures.SampleMp3Path, dst, overwrite: true);
        return dst;
    }

    // ---- unit tests (always run) -----------------------------------------

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Action act = () => new Mp3TagWriter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_NullPath_Throws()
    {
        var w = Build();
        Func<Task> act = () => w.WriteAsync(null!, new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WriteAsync_NullSong_Throws()
    {
        var w = Build();
        Func<Task> act = () => w.WriteAsync("/tmp/x.mp3", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_CancelledToken_Throws()
    {
        var w = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> act = () => w.WriteAsync("/tmp/x.mp3", new SongInfo(1, "t", new[] { "a" }), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WriteAsync_NonexistentFile_ReturnsFailed()
    {
        var w = Build();
        var bogus = Path.Combine(_tempDir, "nope.mp3");
        Directory.CreateDirectory(_tempDir);

        var result = await w.WriteAsync(bogus, new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>();
    }

    // ---- integration tests (fixture-gated) -------------------------------

    [SkippableFact]
    public async Task Mp3_WriteAndReadBack_PreservesUnicodeCjk()
    {
        var path = CopyFixture("cjk.mp3");
        var writer = Build();
        const string title = "\u7ec8\u4e0d\u8d1f";       // CJK
        var artists = new[] { "\u96f6\u4e09\u4e2d\u4e00" };
        const string album = "\u4e91\u5e7b";

        var result = await writer.WriteAsync(
            path,
            new SongInfo(1, title, artists, album),
            CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);

        using var f = TagLib.File.Create(path);
        f.Tag.Title.Should().Be(title);
        f.Tag.Performers.Should().ContainSingle().Which.Should().Be(artists[0]);
        f.Tag.Album.Should().Be(album);
    }

    [SkippableFact]
    public async Task Mp3_MultiArtist_TagsAllPerformers()
    {
        var path = CopyFixture("multi.mp3");
        var writer = Build();
        var artists = new[] { "A", "B", "C" };

        var result = await writer.WriteAsync(
            path,
            new SongInfo(1, "Title", artists),
            CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);

        using var f = TagLib.File.Create(path);
        f.Tag.Performers.Should().Equal(artists);
    }

    [SkippableFact]
    public async Task Mp3_NullAlbum_ClearsAlbumTag()
    {
        var path = CopyFixture("noalbum.mp3");
        var writer = Build();

        var result = await writer.WriteAsync(
            path,
            new SongInfo(1, "Title", new[] { "Artist" }, Album: null),
            CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);

        using var f = TagLib.File.Create(path);
        // TagLib normalises a cleared album to either null or empty string.
        (string.IsNullOrEmpty(f.Tag.Album)).Should().BeTrue();
    }

    [SkippableFact]
    public async Task Mp3_WriteAndRead_UsesId3v24WithUtf8()
    {
        var path = CopyFixture("encoding.mp3");
        var writer = Build();
        const string title = "Caf\u00e9 \u7ec8\u4e0d\u8d1f"; // mixed Latin + CJK

        var result = await writer.WriteAsync(
            path,
            new SongInfo(1, title, new[] { "Artist" }),
            CancellationToken.None);

        result.Should().Be(TagWriteResult.Success.Instance);

        using var f = TagLib.File.Create(path);
        var id3v2 = f.GetTag(TagLib.TagTypes.Id3v2, create: false) as TagLib.Id3v2.Tag;
        id3v2.Should().NotBeNull("ID3v2 tag should be present after write");
        id3v2!.Version.Should().Be(4, "Mp3TagWriter forces ID3v2.4");
        f.Tag.Title.Should().Be(title);
    }
}
