using FluentAssertions;
using FreeBird.Core.Provisioning;

namespace FreeBird.Core.Tests.Provisioning;

public class FlacNotAvailableExceptionTests
{
    [Fact]
    public void MessageCtor_PreservesMessage()
    {
        var ex = new FlacNotAvailableException("flac missing");
        ex.Message.Should().Be("flac missing");
    }

    [Fact]
    public void InnerCtor_PreservesInner()
    {
        var inner = new InvalidOperationException("boom");
        var ex = new FlacNotAvailableException("top", inner);
        ex.InnerException.Should().BeSameAs(inner);
    }
}
