using FluentAssertions;
using FreeBird.Core.Metadata.Internal;

namespace FreeBird.Core.Tests.Metadata.Internal;

public class NetEaseDtoMapperTests
{
    [Fact]
    public void ToSongInfo_RealSong1_Cn_MapsCorrectly()
    {
        var dto = new NetEaseSongDto
        {
            Id = 3367798042,
            Name = "终不负",
            Artists = new List<NetEaseArtistDto>
            {
                new() { Id = 1, Name = "树离suliii_" }
            },
            Album = new NetEaseAlbumDto { Id = 1, Name = "不必完美" }
        };
        var song = NetEaseDtoMapper.ToSongInfo(dto);
        song.Should().NotBeNull();
        song!.MusicId.Should().Be(3367798042);
        song.Title.Should().Be("终不负");
        song.Artists.Should().Equal("树离suliii_");
        song.Album.Should().Be("不必完美");
    }

    [Fact]
    public void ToSongInfo_MultipleArtists_PreservesOrder()
    {
        var dto = new NetEaseSongDto
        {
            Id = 2074977815,
            Name = "Ride It",
            Artists = new()
            {
                new() { Name = "Rohith Pai Kasturi" },
                new() { Name = "IAMPRANN" },
                new() { Name = "IAMPRATHEEK" },
            },
        };
        var song = NetEaseDtoMapper.ToSongInfo(dto)!;
        song.Artists.Should().Equal("Rohith Pai Kasturi", "IAMPRANN", "IAMPRATHEEK");
    }

    [Fact]
    public void ToSongInfo_Null_ReturnsNull()
        => NetEaseDtoMapper.ToSongInfo(null).Should().BeNull();

    [Fact]
    public void ToSongInfo_ZeroId_ReturnsNull()
        => NetEaseDtoMapper.ToSongInfo(new NetEaseSongDto { Id = 0, Name = "T" }).Should().BeNull();

    [Fact]
    public void ToSongInfo_NullArtists_YieldsEmptyList()
    {
        var dto = new NetEaseSongDto { Id = 1, Name = "T", Artists = null };
        var song = NetEaseDtoMapper.ToSongInfo(dto)!;
        song.Artists.Should().BeEmpty();
    }

    [Fact]
    public void ToSongInfo_BlankArtistName_IsFiltered()
    {
        var dto = new NetEaseSongDto
        {
            Id = 1,
            Name = "T",
            Artists = new()
            {
                new() { Name = "A" },
                new() { Name = null },
                new() { Name = "   " },
                new() { Name = "B" },
            },
        };
        NetEaseDtoMapper.ToSongInfo(dto)!.Artists.Should().Equal("A", "B");
    }

    [Fact]
    public void ToSongInfo_NullAlbum_AlbumNull()
    {
        var dto = new NetEaseSongDto { Id = 1, Name = "T", Album = null };
        NetEaseDtoMapper.ToSongInfo(dto)!.Album.Should().BeNull();
    }

    [Fact]
    public void ToSongInfo_EmptyAlbumName_AlbumNull()
    {
        var dto = new NetEaseSongDto { Id = 1, Name = "T", Album = new() { Name = "" } };
        NetEaseDtoMapper.ToSongInfo(dto)!.Album.Should().BeNull();
    }

    [Fact]
    public void ToSongInfo_NullTitle_TitleEmpty()
    {
        // Title in domain is non-nullable; map null → empty
        var dto = new NetEaseSongDto { Id = 1, Name = null };
        NetEaseDtoMapper.ToSongInfo(dto)!.Title.Should().Be("");
    }
}
