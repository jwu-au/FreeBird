using FluentAssertions;
using FreeBird.Core.Provisioning;

namespace FreeBird.Core.Tests.Provisioning;

public class FlacResolutionTests
{
    [Fact]
    public void NotFound_Singleton_HasNullPathAndProvenance()
    {
        FlacResolution.NotFound.Path.Should().BeNull();
        FlacResolution.NotFound.Provenance.Should().Be(FlacBinaryProvenance.NotFound);
        FlacResolution.NotFound.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void Construct_WithPath_IsAvailableTrue()
    {
        var r = new FlacResolution("/usr/bin/flac", FlacBinaryProvenance.Path);
        r.IsAvailable.Should().BeTrue();
        r.Path.Should().Be("/usr/bin/flac");
        r.Provenance.Should().Be(FlacBinaryProvenance.Path);
    }

    [Fact]
    public void Construct_WithNullPath_IsAvailableFalse()
    {
        var r = new FlacResolution(null, FlacBinaryProvenance.NotFound);
        r.IsAvailable.Should().BeFalse();
    }
}
