using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Provisioning;

namespace FreeBird.Core.Tests.Provisioning;

public class InterfaceContractTests
{
    [Fact]
    public void IFlacBinaryResolver_ExtendsIDependency()
        => typeof(IDependency).IsAssignableFrom(typeof(IFlacBinaryResolver)).Should().BeTrue();

    [Fact]
    public void IFlacAutoInstaller_ExtendsIDependency()
        => typeof(IDependency).IsAssignableFrom(typeof(IFlacAutoInstaller)).Should().BeTrue();
}
