using FluentAssertions;
using FreeBird.Core.Metadata;

namespace FreeBird.Core.Tests.Metadata;

public class TagWriteResultTests
{
    [Fact]
    public void Success_IsSingleton()
    {
        TagWriteResult.Success.Instance.Should().BeSameAs(TagWriteResult.Success.Instance);
    }

    [Fact]
    public void Failed_HoldsReason()
    {
        var result = new TagWriteResult.Failed("oops");

        result.SidecarReason.Should().Be("oops");
    }

    [Fact]
    public void Skipped_IsSingleton()
    {
        TagWriteResult.Skipped.Instance.Should().BeSameAs(TagWriteResult.Skipped.Instance);
    }

    [Fact]
    public void PatternMatch_Exhaustive_DispatchesCorrectly()
    {
        Describe(TagWriteResult.Success.Instance).Should().Be("ok");
        Describe(new TagWriteResult.Failed("io-error")).Should().Be("fail:io-error");
        Describe(TagWriteResult.Skipped.Instance).Should().Be("skip");
    }

    private static string Describe(TagWriteResult r) => r switch
    {
        TagWriteResult.Success => "ok",
        TagWriteResult.Failed f => $"fail:{f.SidecarReason}",
        TagWriteResult.Skipped => "skip",
        _ => throw new InvalidOperationException("Unexpected union case")
    };
}
