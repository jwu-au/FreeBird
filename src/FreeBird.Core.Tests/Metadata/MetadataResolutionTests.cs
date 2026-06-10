using FluentAssertions;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Tests.Metadata;

public class MetadataResolutionTests
{
    [Fact]
    public void Success_HoldsSongInfo()
    {
        var song = new SongInfo(42L, "Title", Array.Empty<string>(), null);
        var result = new MetadataResolution.Success(song);

        result.Song.Should().BeSameAs(song);
    }

    [Fact]
    public void Fallback_HoldsReason()
    {
        var result = new MetadataResolution.Fallback("api-not-found");

        result.SidecarReason.Should().Be("api-not-found");
    }

    [Fact]
    public void PatternMatch_Exhaustive_DispatchesCorrectly()
    {
        var song = new SongInfo(1L, "T", Array.Empty<string>(), null);

        Describe(new MetadataResolution.Success(song)).Should().Be("ok:T");
        Describe(new MetadataResolution.Fallback("offline-mode")).Should().Be("fb:offline-mode");
    }

    private static string Describe(MetadataResolution r) => r switch
    {
        MetadataResolution.Success s => $"ok:{s.Song.Title}",
        MetadataResolution.Fallback fb => $"fb:{fb.SidecarReason}",
        _ => throw new InvalidOperationException("Unexpected union case")
    };
}
