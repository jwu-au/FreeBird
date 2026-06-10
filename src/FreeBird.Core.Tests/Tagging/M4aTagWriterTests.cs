using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Metadata;
using FreeBird.Core.Tagging;
using Moq;
using Serilog;

namespace FreeBird.Core.Tests.Tagging;

/// <summary>
/// Tests for <see cref="M4aTagWriter"/>. Unit tests (null/cancel guards) run
/// unconditionally; round-trip integration tests are <see cref="SkippableFact"/>
/// gated on the presence of an M4A fixture at <c>Fixtures/sample.m4a</c>.
///
/// FreeBird does not commit an M4A fixture today (binary fixtures are added on
/// demand and ffmpeg / sox are not assumed to be on the developer's PATH); the
/// integration tests skip cleanly until one is added. End-to-end coverage of
/// the M4A path will arrive with T20.
/// </summary>
public sealed class M4aTagWriterTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"fb-m4a-tag-{Guid.NewGuid():N}");

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

    private static string SampleM4aPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.m4a");

    private static M4aTagWriter Build() => new(NullLogger());

    private string CopyFixture(string name = "track.m4a")
    {
        Skip.IfNot(File.Exists(SampleM4aPath),
            $"M4A fixture not present at {SampleM4aPath} (none committed; covered by T20 end-to-end)");
        Directory.CreateDirectory(_tempDir);
        var dst = Path.Combine(_tempDir, name);
        File.Copy(SampleM4aPath, dst, overwrite: true);
        return dst;
    }

    // ---- unit tests (always run) -----------------------------------------

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        Action act = () => new M4aTagWriter(null!);
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
        Func<Task> act = () => w.WriteAsync("/tmp/x.m4a", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteAsync_CancelledToken_Throws()
    {
        var w = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Func<Task> act = () => w.WriteAsync("/tmp/x.m4a", new SongInfo(1, "t", new[] { "a" }), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WriteAsync_NonexistentFile_ReturnsFailed()
    {
        var w = Build();
        var bogus = Path.Combine(_tempDir, "nope.m4a");
        Directory.CreateDirectory(_tempDir);

        var result = await w.WriteAsync(bogus, new SongInfo(1, "t", new[] { "a" }), CancellationToken.None);

        result.Should().BeOfType<TagWriteResult.Failed>();
    }

    // ---- integration tests (fixture-gated) -------------------------------

    [SkippableFact]
    public async Task M4a_WriteAndReadBack_PreservesUnicodeCjk()
    {
        var path = CopyFixture("cjk.m4a");
        var writer = Build();
        const string title = "\u7ec8\u4e0d\u8d1f";
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
}
