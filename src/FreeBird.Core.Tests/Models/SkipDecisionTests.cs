using FluentAssertions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Models;

public class SkipDecisionTests
{
    [Fact]
    public void Process_HasShouldProcessTrue_NullReason()
    {
        var d = SkipDecision.Process();

        d.ShouldProcess.Should().BeTrue();
        d.Reason.Should().BeNull();
        d.Detail.Should().BeNull();
    }

    [Fact]
    public void Skip_AlreadyDecoded_HasReasonAndDetail()
    {
        var d = SkipDecision.Skip(SkipReason.AlreadyDecoded, "song.flac exists");

        d.ShouldProcess.Should().BeFalse();
        d.Reason.Should().Be(SkipReason.AlreadyDecoded);
        d.Detail.Should().Be("song.flac exists");
    }

    [Fact]
    public void Skip_SourceUnchanged_HasReasonAndDetail()
    {
        var d = SkipDecision.Skip(SkipReason.SourceUnchangedSinceFailure, "size+mtime match sidecar");

        d.ShouldProcess.Should().BeFalse();
        d.Reason.Should().Be(SkipReason.SourceUnchangedSinceFailure);
        d.Detail.Should().Be("size+mtime match sidecar");
    }

    [Fact]
    public void Skip_NotYetStable_HasReasonAndDetail()
    {
        var d = SkipDecision.Skip(SkipReason.NotYetStable, "size changed last cycle");

        d.ShouldProcess.Should().BeFalse();
        d.Reason.Should().Be(SkipReason.NotYetStable);
        d.Detail.Should().Be("size changed last cycle");
    }

    [Fact]
    public void Skip_SourceTooSmall_HasReasonAndDetail()
    {
        var d = SkipDecision.Skip(SkipReason.SourceTooSmall, "size=512 < min=1024");

        d.ShouldProcess.Should().BeFalse();
        d.Reason.Should().Be(SkipReason.SourceTooSmall);
        d.Detail.Should().Be("size=512 < min=1024");
    }
}
