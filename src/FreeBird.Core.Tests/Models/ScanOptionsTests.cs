using FluentAssertions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Models;

public class ScanOptionsTests
{
    [Fact]
    public void Defaults_ExistingFields_MatchSpec()
    {
        var opts = new ScanOptions(new[] { "in" }, "out");

        opts.InputDirectories.Should().ContainSingle().Which.Should().Be("in");
        opts.OutputDirectory.Should().Be("out");
        opts.Integrity.Should().Be(IntegrityLevel.Auto);
        opts.Concurrency.Should().Be(2);
        opts.OnCollision.Should().Be(CollisionPolicy.Skip);
    }

    [Fact]
    public void InputDirectories_AcceptsMultipleEntries()
    {
        // v3.4 T13: ScanOptions now supports a list of input directories.
        var opts = new ScanOptions(new[] { "in1", "in2", "in3" }, "out");

        opts.InputDirectories.Should().BeEquivalentTo(new[] { "in1", "in2", "in3" });
        opts.InputDirectories.Count.Should().Be(3);
    }

    [Fact]
    public void Defaults_FiveMetadataFields_MatchSpec()
    {
        var opts = new ScanOptions(new[] { "in" }, "out");

        opts.NamingTemplate.Should().Be("{artist} - {title}");
        opts.Offline.Should().BeFalse();
        opts.ApiTimeoutSeconds.Should().Be(10);
        opts.ApiRateLimit.Should().Be(0);
        opts.WriteTags.Should().BeTrue();
    }

    [Fact]
    public void WithBlocks_OverrideMetadataFields()
    {
        var a = new ScanOptions(new[] { "in" }, "out");
        var b = a with
        {
            NamingTemplate = "{musicId}",
            Offline = true,
            ApiTimeoutSeconds = 30,
            ApiRateLimit = 5,
            WriteTags = true,
        };

        a.NamingTemplate.Should().Be("{artist} - {title}");
        a.Offline.Should().BeFalse();
        b.NamingTemplate.Should().Be("{musicId}");
        b.Offline.Should().BeTrue();
        b.ApiTimeoutSeconds.Should().Be(30);
        b.ApiRateLimit.Should().Be(5);
        b.WriteTags.Should().BeTrue();
    }
}
