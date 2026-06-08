using FluentAssertions;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Tests.Abstractions;

public class IDependencyTests
{
    [Fact]
    public void IDependency_IsPublic()
    {
        typeof(IDependency).IsPublic.Should().BeTrue();
    }

    [Fact]
    public void IDependency_HasNoMembers()
    {
        typeof(IDependency)
            .GetMembers()
            .Where(m => m.DeclaringType == typeof(IDependency))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void IDependency_CanBeImplemented()
    {
        typeof(TestService)
            .GetInterfaces()
            .Should()
            .Contain(typeof(IDependency));
    }

    private sealed class TestService : IDependency { }
}
