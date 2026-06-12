using FluentAssertions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Models;

public class ModelTests
{
    [Fact]
    public void AudioFormat_Enum_HasAllRequiredValues()
    {
        Enum.GetNames<AudioFormat>().Should().BeEquivalentTo(
            new[] { "Unknown", "Mp3", "Flac", "M4a" });
    }

    [Fact]
    public void IntegrityLevel_Enum_HasAllRequiredValues()
    {
        Enum.GetNames<IntegrityLevel>().Should().BeEquivalentTo(
            new[] { "Off", "L1", "L3", "Auto" });
    }

    [Fact]
    public void IntegrityResult_Passed_FactoryProducesOkResult()
    {
        var r = IntegrityResult.Passed(IntegrityLevel.L3);
        r.Ok.Should().BeTrue();
        r.LevelApplied.Should().Be(IntegrityLevel.L3);
        r.Reason.Should().BeNull();
    }

    [Fact]
    public void IntegrityResult_Failed_FactoryProducesFailureWithReason()
    {
        var r = IntegrityResult.Failed(IntegrityLevel.L1, "truncated");
        r.Ok.Should().BeFalse();
        r.LevelApplied.Should().Be(IntegrityLevel.L1);
        r.Reason.Should().Be("truncated");
    }

    [Fact]
    public void ScanSummary_HasFailures_TrueWhenAnyFailureCategoryNonzero()
    {
        new ScanSummary(1, 1, 0, 0, 0, 0, TimeSpan.Zero).HasFailures.Should().BeFalse();
        new ScanSummary(2, 1, 0, 1, 0, 0, TimeSpan.Zero).HasFailures.Should().BeTrue();   // UnknownFormat
        new ScanSummary(2, 1, 0, 0, 1, 0, TimeSpan.Zero).HasFailures.Should().BeTrue();   // IntegrityFailed
        new ScanSummary(2, 1, 0, 0, 0, 1, TimeSpan.Zero).HasFailures.Should().BeTrue();   // Errors
    }

    [Fact]
    public void ScanOptions_DefaultsAreSpecCompliant()
    {
        var opts = new ScanOptions(new[] { "in" }, "out");
        opts.Integrity.Should().Be(IntegrityLevel.Auto);
        opts.Concurrency.Should().Be(2);
        opts.OnCollision.Should().Be(CollisionPolicy.Skip);
    }
}
