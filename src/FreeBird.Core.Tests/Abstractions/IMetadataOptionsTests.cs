using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Abstractions;

public class IMetadataOptionsTests
{
    [Fact]
    public void ScanOptions_ImplementsIMetadataOptions()
    {
        var opts = new ScanOptions(new[] { "in" }, "out");

        var asMetadata = opts as IMetadataOptions;

        asMetadata.Should().NotBeNull();
        asMetadata!.NamingTemplate.Should().Be("{artist} - {title}");
        asMetadata.Offline.Should().BeFalse();
        asMetadata.ApiTimeoutSeconds.Should().Be(10);
        asMetadata.ApiRateLimit.Should().Be(0);
        asMetadata.WriteTags.Should().BeTrue();
    }

    [Fact]
    public void WatchOptions_ImplementsIMetadataOptions()
    {
        var opts = new WatchOptions(new[] { "in" }, "out");

        var asMetadata = opts as IMetadataOptions;

        asMetadata.Should().NotBeNull();
        asMetadata!.NamingTemplate.Should().Be("{artist} - {title}");
        asMetadata.Offline.Should().BeFalse();
        asMetadata.ApiTimeoutSeconds.Should().Be(10);
        asMetadata.ApiRateLimit.Should().Be(0);
        asMetadata.WriteTags.Should().BeTrue();
    }

    [Fact]
    public void IMetadataOptions_DoesNotExtend_IDependency()
    {
        typeof(IDependency).IsAssignableFrom(typeof(IMetadataOptions)).Should().BeFalse(
            "IMetadataOptions is a pure shape marker shared by options records, not a DI service");
    }
}
