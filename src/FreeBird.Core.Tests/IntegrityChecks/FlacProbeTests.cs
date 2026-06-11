using FluentAssertions;
using FreeBird.Core.Integrity;
using FreeBird.Core.Provisioning;
using Moq;

namespace FreeBird.Core.Tests.IntegrityChecks;

public class FlacProbeTests
{
    [Fact]
    public async Task IsAvailable_ResolverFindsFlac_ReturnsTrue()
    {
        var resolver = new Mock<IFlacBinaryResolver>();
        resolver.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FlacResolution("/usr/bin/flac", FlacBinaryProvenance.Path));
        var probe = new FlacProbe(resolver.Object);
        (await probe.IsAvailableAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task IsAvailable_ResolverNotFound_ReturnsFalse()
    {
        var resolver = new Mock<IFlacBinaryResolver>();
        resolver.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FlacResolution.NotFound);
        var probe = new FlacProbe(resolver.Object);
        (await probe.IsAvailableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsAvailable_CachesPositiveResult()
    {
        var resolver = new Mock<IFlacBinaryResolver>();
        resolver.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FlacResolution("/x", FlacBinaryProvenance.Path));
        var probe = new FlacProbe(resolver.Object);
        await probe.IsAvailableAsync();
        await probe.IsAvailableAsync();
        await probe.IsAvailableAsync();
        resolver.Verify(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsAvailable_CachesNegativeResult()
    {
        var resolver = new Mock<IFlacBinaryResolver>();
        resolver.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(FlacResolution.NotFound);
        var probe = new FlacProbe(resolver.Object);
        (await probe.IsAvailableAsync()).Should().BeFalse();
        (await probe.IsAvailableAsync()).Should().BeFalse();
        resolver.Verify(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsAvailable_Concurrent_OnlyOneResolverCall()
    {
        var resolver = new Mock<IFlacBinaryResolver>();
        resolver.Setup(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()))
            .Returns(async () => { await Task.Delay(50); return new FlacResolution("/x", FlacBinaryProvenance.Path); });
        var probe = new FlacProbe(resolver.Object);
        var tasks = Enumerable.Range(0, 10).Select(_ => probe.IsAvailableAsync()).ToArray();
        await Task.WhenAll(tasks);
        tasks.Should().AllSatisfy(t => t.Result.Should().BeTrue());
        resolver.Verify(r => r.ResolveFlacAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Ctor_NullResolver_Throws()
    {
        Action act = () => new FlacProbe(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("resolver");
    }
}
