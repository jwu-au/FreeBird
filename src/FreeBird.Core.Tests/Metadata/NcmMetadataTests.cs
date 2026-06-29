using System.Text.Json;
using FluentAssertions;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Tests.Metadata;

public class NcmMetadataTests
{
    [Fact]
    public void Parse_FullJson_PopulatesAllFields()
    {
        const string json =
            """
            {"musicId":123,"musicName":"Poison","artist":[["Taylor Grey",456]],"album":"Poison","albumId":789,"bitrate":999000,"duration":210000,"format":"flac"}
            """;

        var meta = NcmMetadataParser.Parse(json);

        meta.MusicName.Should().Be("Poison");
        meta.Album.Should().Be("Poison");
        meta.Artists.Should().ContainSingle().Which.Should().Be("Taylor Grey");
        meta.Format.Should().Be("flac");
        meta.Bitrate.Should().Be(999000);
        meta.Duration.Should().Be(210000L);
    }

    [Fact]
    public void Parse_MultipleArtists_PreservesOrder()
    {
        const string json =
            """
            {"musicName":"Duet","artist":[["A",1],["B",2]],"format":"mp3"}
            """;

        var meta = NcmMetadataParser.Parse(json);

        meta.Artists.Should().Equal("A", "B");
    }

    [Fact]
    public void Parse_MissingOptionalFields_YieldsNulls()
    {
        const string json =
            """
            {"musicName":"Lonely","artist":[["Solo",7]],"format":"flac"}
            """;

        var meta = NcmMetadataParser.Parse(json);

        meta.Album.Should().BeNull();
        meta.Bitrate.Should().BeNull();
        meta.Duration.Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyArtistArray_YieldsEmptyList()
    {
        const string json =
            """
            {"musicName":"Anon","artist":[],"format":"flac"}
            """;

        var meta = NcmMetadataParser.Parse(json);

        meta.Artists.Should().NotBeNull();
        meta.Artists.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MissingArtistField_YieldsEmptyList()
    {
        const string json =
            """
            {"musicName":"NoArtistKey","format":"flac"}
            """;

        var meta = NcmMetadataParser.Parse(json);

        meta.Artists.Should().NotBeNull();
        meta.Artists.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MalformedArtistEntries_AreSkippedWithoutThrowing()
    {
        // First entry has only a name (1-element), second is well-formed,
        // third has a non-string element 0 — only the well-formed one survives.
        const string json =
            """
            {"musicName":"Mixed","artist":[["OnlyName"],["Good",2],[42,3]],"format":"flac"}
            """;

        var meta = NcmMetadataParser.Parse(json);

        meta.Artists.Should().Equal("Good");
    }

    [Fact]
    public void Parse_BitrateAsJsonNumber_IsRead()
    {
        const string json =
            """
            {"musicName":"BR","artist":[["X",1]],"bitrate":320000,"format":"mp3"}
            """;

        var meta = NcmMetadataParser.Parse(json);

        meta.Bitrate.Should().Be(320000);
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsJsonException()
    {
        const string json = "{ not valid json ";

        var act = () => NcmMetadataParser.Parse(json);

        act.Should().Throw<JsonException>();
    }
}
