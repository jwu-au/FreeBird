using System.Linq;
using FluentAssertions;
using FreeBird.Cli.Service;
using FreeBird.Core.Abstractions;
using FreeBird.Core.DependencyInjection;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T08 — Contract + behaviour tests for <see cref="IElevationChecker"/> and
/// <see cref="NonWindowsElevationChecker"/>.
///
/// These tests pin down:
///   (1) the non-Windows checker unconditionally returns <c>false</c> so the
///       install/uninstall guards trip with a clean error on non-Windows hosts;
///   (2) calling <c>IsAdmin()</c> repeatedly returns the same value (idempotency);
///   (3) <see cref="IElevationChecker"/> extends <see cref="IDependency"/> so DI
///       registration works once <c>CliServiceModule</c> (T14) lands;
///   (4) no concrete <see cref="IElevationChecker"/> implementation lives in the
///       Core assembly (B-5/B-6 invariant — elevation impls stay in Cli);
///   (5) <see cref="NonWindowsElevationChecker"/> lives in the Cli assembly, not
///       Core (belt-and-suspenders companion to the reflection guard);
///   (6) the non-Windows checker is <c>sealed</c> to lock down extensibility.
/// </summary>
public class NonWindowsElevationCheckerTests
{
    [Fact]
    public void IsAdmin_ReturnsFalse_AlwaysRegardlessOfProcessElevation()
    {
        IElevationChecker sut = new NonWindowsElevationChecker();

        sut.IsAdmin().Should().BeFalse(
            "the non-Windows fallback must never claim admin so install/uninstall guards trip cleanly");
    }

    [Fact]
    public void IsAdmin_IsIdempotent_MultipleCallsReturnSameValue()
    {
        IElevationChecker sut = new NonWindowsElevationChecker();

        var first = sut.IsAdmin();
        var second = sut.IsAdmin();

        first.Should().Be(second);
        second.Should().BeFalse();
    }

    [Fact]
    public void IElevationChecker_ExtendsIDependency()
    {
        typeof(IDependency).IsAssignableFrom(typeof(IElevationChecker))
            .Should().BeTrue("IElevationChecker must be DI-discoverable via the IDependency marker");
    }

    [Fact]
    public void IElevationChecker_HasNoConcreteImplementation_InCoreAssembly()
    {
        var coreAssembly = typeof(CoreModule).Assembly;
        var offenders = coreAssembly.GetTypes()
            .Where(t => typeof(IElevationChecker).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract)
            .ToArray();

        offenders.Should().BeEmpty(
            "T08 forbids any IElevationChecker implementation in FreeBird.Core (B-5/B-6 invariant)");
    }

    [Fact]
    public void NonWindowsElevationChecker_IsCliAssembly_NotCore()
    {
        typeof(NonWindowsElevationChecker).Assembly
            .Should().NotBeSameAs(typeof(CoreModule).Assembly,
                "NonWindowsElevationChecker must live in FreeBird.Cli so CoreModule's assembly scan never auto-registers it");
    }

    [Fact]
    public void NonWindowsElevationChecker_IsSealed()
    {
        typeof(NonWindowsElevationChecker).IsSealed
            .Should().BeTrue("the non-Windows checker has no extension points; T14 swaps the impl at DI-binding time");
    }
}
