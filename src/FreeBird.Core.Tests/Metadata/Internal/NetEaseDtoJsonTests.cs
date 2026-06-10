using System.Text.Json;
using FluentAssertions;
using FreeBird.Core.Metadata.Internal;

namespace FreeBird.Core.Tests.Metadata.Internal;

public class NetEaseDtoJsonTests
{
    [Fact]
    public void Deserialize_RealNetEaseResponse_Shape1()
    {
        var json = """
            {
              "songs": [
                {
                  "id": 3367798042,
                  "name": "终不负",
                  "artists": [{ "id": 1, "name": "树离suliii_" }],
                  "album": { "id": 2, "name": "不必完美" },
                  "someUnknownField": "ignored"
                }
              ],
              "code": 200,
              "equalizers": {}
            }
            """;
        var resp = JsonSerializer.Deserialize<NetEaseResponseDto>(json);
        resp.Should().NotBeNull();
        resp!.Code.Should().Be(200);
        resp.Songs.Should().HaveCount(1);
        resp.Songs![0].Id.Should().Be(3367798042);
        resp.Songs[0].Name.Should().Be("终不负");
        resp.Songs[0].Artists.Should().HaveCount(1);
        resp.Songs[0].Artists![0].Name.Should().Be("树离suliii_");
        resp.Songs[0].Album!.Name.Should().Be("不必完美");
    }

    [Fact]
    public void Deserialize_EmptySongs_OkAndEmpty()
    {
        var json = """{ "songs": [], "code": 200 }""";
        var resp = JsonSerializer.Deserialize<NetEaseResponseDto>(json);
        resp!.Code.Should().Be(200);
        resp.Songs.Should().BeEmpty();
    }

    [Fact]
    public void Deserialize_NullSongs_OkAndNull()
    {
        var json = """{ "code": 200 }""";
        var resp = JsonSerializer.Deserialize<NetEaseResponseDto>(json);
        resp!.Code.Should().Be(200);
        resp.Songs.Should().BeNull();
    }

    [Fact]
    public void Deserialize_GarbageJson_Throws()
    {
        Action act = () => JsonSerializer.Deserialize<NetEaseResponseDto>("this is not json");
        act.Should().Throw<JsonException>();
    }
}
