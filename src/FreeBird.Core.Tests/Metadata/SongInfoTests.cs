using FluentAssertions;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Tests.Metadata;

public class SongInfoTests
{
    [Fact]
    public void Constructor_AssignsAllFields()
    {
        var artists = new[] { "Rohith Pai Kasturi", "IAMPRANN", "IAMPRATHEEK" };
        var info = new SongInfo(123456L, "终不负", artists, "OST Album");

        info.MusicId.Should().Be(123456L);
        info.Title.Should().Be("终不负");
        info.Artists.Should().HaveCount(3);
        info.Artists[0].Should().Be("Rohith Pai Kasturi");
        info.Artists[1].Should().Be("IAMPRANN");
        info.Artists[2].Should().Be("IAMPRATHEEK");
        info.Album.Should().Be("OST Album");
    }

    [Fact]
    public void Constructor_AlbumNullable_AcceptsNull()
    {
        var info = new SongInfo(1L, "Title", new[] { "Artist" });

        info.Album.Should().BeNull();
    }

    [Fact]
    public void EmptyArtists_AcceptsEmptyList()
    {
        var info = new SongInfo(1L, "Title", Array.Empty<string>(), null);

        info.Artists.Should().NotBeNull();
        info.Artists.Should().BeEmpty();
    }

    [Fact]
    public void Equality_RecordSemantics_TwoIdenticalAreEqual()
    {
        // Records compare member-by-member, but IReadOnlyList<string> (here a string[])
        // uses reference equality by default. Two records with logically-equal but
        // separate array instances are therefore NOT equal. This test pins down that
        // surprising behavior so future selves don't assume value-equality on collections.
        var a = new SongInfo(1L, "T", new[] { "X" }, "A");
        var b = new SongInfo(1L, "T", new[] { "X" }, "A");

        a.Should().NotBe(b);
    }

    [Fact]
    public void Equality_SameArtistListInstance_IsEqual()
    {
        // Confirms the converse: when the SAME collection instance is shared, the
        // records ARE equal. This is the only way to get record equality on SongInfo.
        var artists = new[] { "X" };
        var a = new SongInfo(1L, "T", artists, "A");
        var b = new SongInfo(1L, "T", artists, "A");

        a.Should().Be(b);
    }
}
