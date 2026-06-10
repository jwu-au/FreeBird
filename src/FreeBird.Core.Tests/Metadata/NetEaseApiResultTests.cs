using FluentAssertions;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Tests.Metadata;

public class NetEaseApiResultTests
{
    [Fact]
    public void Success_HoldsSongInfo()
    {
        var song = new SongInfo(42L, "Title", Array.Empty<string>(), null);
        var result = new NetEaseApiResult.Success(song);

        result.Song.Should().BeSameAs(song);
    }

    [Fact]
    public void NotFound_HoldsMusicId()
    {
        var result = new NetEaseApiResult.NotFound(123L);

        result.MusicId.Should().Be(123L);
    }

    [Fact]
    public void NetworkError_HoldsMessage()
    {
        var result = new NetEaseApiResult.NetworkError("connection refused");

        result.Message.Should().Be("connection refused");
    }

    [Fact]
    public void Timeout_HoldsElapsed()
    {
        var elapsed = TimeSpan.FromSeconds(10);
        var result = new NetEaseApiResult.Timeout(elapsed);

        result.Elapsed.Should().Be(elapsed);
    }

    [Fact]
    public void DeserializationError_HoldsMessage()
    {
        var result = new NetEaseApiResult.DeserializationError("invalid json at line 3");

        result.Message.Should().Be("invalid json at line 3");
    }

    [Fact]
    public void PatternMatch_Exhaustive_CompilesAndDispatches()
    {
        var song = new SongInfo(1L, "T", Array.Empty<string>(), null);

        Describe(new NetEaseApiResult.Success(song)).Should().Be("ok:T");
        Describe(new NetEaseApiResult.NotFound(7L)).Should().Be("miss:7");
        Describe(new NetEaseApiResult.NetworkError("dns")).Should().Be("net:dns");
        Describe(new NetEaseApiResult.Timeout(TimeSpan.FromSeconds(10))).Should().Be("slow:10");
        Describe(new NetEaseApiResult.DeserializationError("bad")).Should().Be("bad:bad");
    }

    private static string Describe(NetEaseApiResult r) => r switch
    {
        NetEaseApiResult.Success s => $"ok:{s.Song.Title}",
        NetEaseApiResult.NotFound nf => $"miss:{nf.MusicId}",
        NetEaseApiResult.NetworkError ne => $"net:{ne.Message}",
        NetEaseApiResult.Timeout t => $"slow:{t.Elapsed.TotalSeconds}",
        NetEaseApiResult.DeserializationError de => $"bad:{de.Message}",
        _ => throw new InvalidOperationException("Unexpected union case")
    };
}
