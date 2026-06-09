using System;
using System.Threading;
using FluentAssertions;
using FreeBird.Cli;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Serilog;
using Serilog.Events;

namespace FreeBird.Cli.Tests;

/// <summary>
/// T12 — Unit tests for <see cref="CancellationCoordinator"/>: the double-Ctrl-C escalation
/// state machine. We test the coordinator in isolation (no real OS signal subscription) by
/// invoking <c>OnCancelRequested</c> / <c>OnHardSignalRequested</c> directly and using a
/// <see cref="FakeTimeProvider"/> to deterministically advance the 5-second grace timer.
/// </summary>
public class CancellationCoordinatorTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private static (CancellationCoordinator coord, FakeTimeProvider clock, Mock<ILogger> log) Build(
        TimeSpan? grace = null)
    {
        var clock = new FakeTimeProvider();
        var log = new Mock<ILogger>();
        var coord = new CancellationCoordinator(clock, log.Object, grace ?? Grace);
        return (coord, clock, log);
    }

    [Fact]
    public void Initial_BothTokensNotCancelled()
    {
        var (coord, _, _) = Build();
        using (coord)
        {
            coord.Graceful.IsCancellationRequested.Should().BeFalse();
            coord.Hard.IsCancellationRequested.Should().BeFalse();
            coord.SignalCount.Should().Be(0);
        }
    }

    [Fact]
    public void FirstSignal_CancelsGraceful_NotHard()
    {
        var (coord, _, _) = Build();
        using (coord)
        {
            coord.OnCancelRequested();

            coord.Graceful.IsCancellationRequested.Should().BeTrue();
            coord.Hard.IsCancellationRequested.Should().BeFalse();
            coord.SignalCount.Should().Be(1);
        }
    }

    [Fact]
    public void FirstSignal_StartsTimer_AdvanceClock_HardCancelled()
    {
        var (coord, clock, _) = Build();
        using (coord)
        {
            coord.OnCancelRequested();
            coord.Hard.IsCancellationRequested.Should().BeFalse();

            // Advance past the 5s grace period — timer should fire and cancel the hard token.
            clock.Advance(TimeSpan.FromSeconds(5));

            coord.Hard.IsCancellationRequested.Should().BeTrue();
        }
    }

    [Fact]
    public void FirstSignal_BeforeTimerExpiry_NoHardCancel()
    {
        var (coord, clock, _) = Build();
        using (coord)
        {
            coord.OnCancelRequested();

            // Advance only 4s — should NOT fire the hard cancel yet.
            clock.Advance(TimeSpan.FromSeconds(4));

            coord.Hard.IsCancellationRequested.Should().BeFalse();
            coord.Graceful.IsCancellationRequested.Should().BeTrue();
        }
    }

    [Fact]
    public void SecondSignal_CancelsHard_Immediately()
    {
        var (coord, clock, _) = Build();
        using (coord)
        {
            coord.OnCancelRequested();
            coord.OnCancelRequested();

            // No clock advance — hard should already be cancelled.
            coord.Hard.IsCancellationRequested.Should().BeTrue();
            coord.SignalCount.Should().Be(2);
            // Even if we advance later, no extra cancellations / no exception.
            clock.Advance(TimeSpan.FromSeconds(10));
            coord.Hard.IsCancellationRequested.Should().BeTrue();
        }
    }

    [Fact]
    public void SecondSignal_AfterGraceful_TimerDoesNotDoubleCancel()
    {
        var (coord, clock, _) = Build();
        using (coord)
        {
            coord.OnCancelRequested();   // count = 1, graceful cancelled, timer armed
            coord.OnCancelRequested();   // count = 2, hard cancelled, timer disposed

            // Capture state immediately after second signal.
            coord.Hard.IsCancellationRequested.Should().BeTrue();

            // Advance past the grace period — the disposed timer must NOT throw or do anything
            // observable. (If the timer were still live and tried to .Cancel() a disposed CTS,
            // that would be silently absorbed by .NET — but disposing the timer is still the
            // right behavior to avoid leaks.)
            var act = () => clock.Advance(TimeSpan.FromSeconds(10));
            act.Should().NotThrow();

            coord.Hard.IsCancellationRequested.Should().BeTrue();
            coord.SignalCount.Should().Be(2);
        }
    }

    [Fact]
    public void SignalCount_AccurateAfterMultipleCalls()
    {
        var (coord, _, _) = Build();
        using (coord)
        {
            for (int i = 0; i < 5; i++)
            {
                coord.OnCancelRequested();
            }
            coord.SignalCount.Should().Be(5);
            // 3rd+ Ctrl-C is silently ignored — hard is still cancelled, no exception.
            coord.Hard.IsCancellationRequested.Should().BeTrue();
        }
    }

    [Fact]
    public void OnHardSignalRequested_ImmediatelyCancelsHard_BumpsCount()
    {
        var (coord, _, _) = Build();
        using (coord)
        {
            coord.OnHardSignalRequested();

            coord.Hard.IsCancellationRequested.Should().BeTrue();
            coord.SignalCount.Should().Be(1);
        }
    }

    [Fact]
    public void OnHardSignalRequested_DoesNotCancelGraceful()
    {
        // Design choice: SIGTERM cancels hard only. Orchestrator's Parallel.ForEachAsync watches
        // hard; graceful is unused since SIGTERM means "stop now, no grace period."
        var (coord, _, _) = Build();
        using (coord)
        {
            coord.OnHardSignalRequested();

            coord.Hard.IsCancellationRequested.Should().BeTrue();
            coord.Graceful.IsCancellationRequested.Should().BeFalse();
        }
    }

    [Fact]
    public void Dispose_DoesNotThrow_EvenIfNoSignalsReceived()
    {
        var (coord, _, _) = Build();
        var act = () => coord.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (coord, _, _) = Build();
        coord.Dispose();
        var act = () => coord.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_AfterFirstSignal_DoesNotThrow()
    {
        var (coord, _, _) = Build();
        coord.OnCancelRequested();
        var act = () => coord.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Tokens_RemainAccessible_AfterDispose()
    {
        var (coord, _, _) = Build();
        var graceful = coord.Graceful;
        var hard = coord.Hard;
        coord.OnCancelRequested();
        coord.OnCancelRequested();  // hard cancel
        coord.Dispose();

        // These reads must not throw — they did before the N1 fix.
        Action readGraceful = () => { _ = coord.Graceful.IsCancellationRequested; };
        Action readHard = () => { _ = coord.Hard.IsCancellationRequested; };
        readGraceful.Should().NotThrow();
        readHard.Should().NotThrow();
        coord.Graceful.IsCancellationRequested.Should().BeTrue(because: "cancelled state survives dispose");
        coord.Hard.IsCancellationRequested.Should().BeTrue();
    }
}
