using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Integrity;
using Moq;

namespace FreeBird.Core.Tests.IntegrityChecks;

public class FlacProbeTests
{
    [Fact]
    public async Task IsAvailableAsync_RunnerReturnsZeroExit_ReturnsTrue()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync("flac", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new ProcessResult(0, "flac 1.5.0", ""));

        var sut = new FlacProbe(runner.Object);
        (await sut.IsAvailableAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task IsAvailableAsync_RunnerReturnsNonzeroExit_ReturnsFalse()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync("flac", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new ProcessResult(127, "", "command not found"));

        var sut = new FlacProbe(runner.Object);
        (await sut.IsAvailableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsAvailableAsync_RunnerThrowsWin32Exception_ReturnsFalse()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync("flac", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new Win32Exception("binary not found"));

        var sut = new FlacProbe(runner.Object);
        (await sut.IsAvailableAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsAvailableAsync_CalledTwice_RunnerCalledOnce_Cached()
    {
        var runner = new Mock<IProcessRunner>();
        runner.Setup(r => r.RunAsync("flac", It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new ProcessResult(0, "v", ""));

        var sut = new FlacProbe(runner.Object);
        var r1 = await sut.IsAvailableAsync();
        var r2 = await sut.IsAvailableAsync();

        r1.Should().BeTrue();
        r2.Should().BeTrue();
        runner.Verify(r => r.RunAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
                      Times.Once);
    }

    [Fact]
    public void Constructor_NullRunner_Throws()
    {
        Action act = () => _ = new FlacProbe(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
