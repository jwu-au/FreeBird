using FluentAssertions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Models;

public class WatchOptionsTests
{
    [Fact]
    public void Defaults_ProduceExpectedValues()
    {
        var opts = new WatchOptions("in", "out");

        opts.InputDir.Should().Be("in");
        opts.OutputDir.Should().Be("out");
        opts.Integrity.Should().Be(IntegrityLevel.Auto);
        opts.Concurrency.Should().Be(4);
        opts.Collision.Should().Be(CollisionPolicy.Skip);
        opts.PollInterval.Should().Be(TimeSpan.FromSeconds(5));
        opts.StabilityChecks.Should().Be(2);
        opts.MinFileSizeBytes.Should().Be(1024);
        opts.SkipInitialScan.Should().BeFalse();
    }

    [Fact]
    public void WithBlocks_AreImmutable()
    {
        var a = new WatchOptions("in", "out");
        var b = a with { Concurrency = 8, SkipInitialScan = true };

        a.Concurrency.Should().Be(4);
        a.SkipInitialScan.Should().BeFalse();
        b.Concurrency.Should().Be(8);
        b.SkipInitialScan.Should().BeTrue();
        b.Should().NotBeSameAs(a);
    }

    [Fact]
    public void Required_InputDir_NullThrows()
    {
        Action act = () => new WatchOptions(null!, "out");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Required_OutputDir_NullThrows()
    {
        Action act = () => new WatchOptions("in", null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Negative_PollInterval_Throws()
    {
        Action act = () => new WatchOptions("in", "out", PollInterval: TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Negative_StabilityChecks_Throws()
    {
        Action act = () => new WatchOptions("in", "out", StabilityChecks: -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Negative_MinFileSizeBytes_Throws()
    {
        Action act = () => new WatchOptions("in", "out", MinFileSizeBytes: -1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
