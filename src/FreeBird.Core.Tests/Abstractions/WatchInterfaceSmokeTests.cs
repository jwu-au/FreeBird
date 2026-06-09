using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FreeBird.Core.Abstractions;
using FreeBird.Core.Models;

namespace FreeBird.Core.Tests.Abstractions;

/// <summary>
/// Smoke tests for the four watch-mode abstractions introduced in T03.
/// Reflection-verify the signatures so later tasks can't accidentally drift the contracts.
/// </summary>
public class WatchInterfaceSmokeTests
{
    [Fact]
    public void ISidecarReader_HasTryReadAsync_Method()
    {
        var method = typeof(ISidecarReader).GetMethod(nameof(ISidecarReader.TryReadAsync));
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<SidecarRecord?>));
        var parameters = method.GetParameters();
        parameters.Should().HaveCount(2);
        parameters[0].ParameterType.Should().Be(typeof(string));
        parameters[1].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void ICompletionDetector_HasIsStableAndForget_Methods()
    {
        var isStable = typeof(ICompletionDetector).GetMethod(nameof(ICompletionDetector.IsStableAsync));
        isStable.Should().NotBeNull();
        isStable!.ReturnType.Should().Be(typeof(Task<bool>));
        var isStableParams = isStable.GetParameters();
        isStableParams.Should().HaveCount(3);
        isStableParams[0].ParameterType.Should().Be(typeof(string));
        isStableParams[1].ParameterType.Should().Be(typeof(int));
        isStableParams[2].ParameterType.Should().Be(typeof(CancellationToken));

        var forget = typeof(ICompletionDetector).GetMethod(nameof(ICompletionDetector.Forget));
        forget.Should().NotBeNull();
        forget!.ReturnType.Should().Be(typeof(void));
        var forgetParams = forget.GetParameters();
        forgetParams.Should().HaveCount(1);
        forgetParams[0].ParameterType.Should().Be(typeof(string));
    }

    [Fact]
    public void ISkipDecider_HasDecideAsync_Method()
    {
        var method = typeof(ISkipDecider).GetMethod(nameof(ISkipDecider.DecideAsync));
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<SkipDecision>));
        var parameters = method.GetParameters();
        parameters.Should().HaveCount(3);
        parameters[0].ParameterType.Should().Be(typeof(string));
        parameters[1].ParameterType.Should().Be(typeof(WatchOptions));
        parameters[2].ParameterType.Should().Be(typeof(CancellationToken));
    }

    [Fact]
    public void IWatchOrchestrator_HasRunAsync_Method_WithTwoCancellationTokens()
    {
        var method = typeof(IWatchOrchestrator).GetMethod(nameof(IWatchOrchestrator.RunAsync));
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<ScanSummary>));
        var parameters = method.GetParameters();
        parameters.Should().HaveCount(3);
        parameters[0].ParameterType.Should().Be(typeof(WatchOptions));
        parameters[1].ParameterType.Should().Be(typeof(CancellationToken));
        parameters[2].ParameterType.Should().Be(typeof(CancellationToken));
    }
}
