using System;
using System.Linq;
using FluentAssertions;
using FreeBird.Cli.Service;
using FreeBird.Core.Abstractions;
using FreeBird.Core.DependencyInjection;
using Xunit;

namespace FreeBird.Cli.Tests.Service;

/// <summary>
/// T11 — OS-agnostic contract tests for <see cref="IEventLogWriter"/> and its
/// cross-platform fallback <see cref="NotSupportedEventLogWriter"/>.
///
/// These tests run on every OS (the Windows-only smoke tests live in a separate
/// file that is excluded from the macOS/Linux compile set). They pin down:
///   (1) both members of <see cref="NotSupportedEventLogWriter"/> throw
///       <see cref="PlatformNotSupportedException"/> with the shared friendly §10
///       message so users on non-Windows hosts get a clean error;
///   (2) the stub implements <see cref="IEventLogWriter"/> and is <c>sealed</c>;
///   (3) <see cref="IEventLogWriter"/> extends <see cref="IDependency"/> so DI
///       registration works once <c>CliServiceModule</c> (T14) lands;
///   (4) the interface exposes ONLY <c>CreateSourceIfMissing</c> +
///       <c>RemoveSourceIfExists</c> and NO <c>Write</c> method — the M-5 invariant
///       (runtime logging is exclusively via <c>Serilog.Sinks.EventLog</c>);
///   (5) no concrete <see cref="IEventLogWriter"/> implementation lives in the
///       Core assembly (B-5/B-6 invariant — event-log impls stay in Cli).
/// </summary>
public class NotSupportedEventLogWriterTests
{
    private const string WindowsOnlyFragment = "'fb service' is Windows-only.";
    private const string ReadmePointerFragment = "See README §Service";

    [Fact]
    public void CreateSourceIfMissing_Throws_PlatformNotSupported()
    {
        IEventLogWriter sut = new NotSupportedEventLogWriter();
        Action act = () => sut.CreateSourceIfMissing("FreeBird", "Application");

        act.Should().Throw<PlatformNotSupportedException>()
            .Which.Message.Should().Contain(WindowsOnlyFragment).And.Contain(ReadmePointerFragment);
    }

    [Fact]
    public void RemoveSourceIfExists_Throws_PlatformNotSupported()
    {
        IEventLogWriter sut = new NotSupportedEventLogWriter();
        Action act = () => sut.RemoveSourceIfExists("FreeBird");

        act.Should().Throw<PlatformNotSupportedException>()
            .Which.Message.Should().Contain(WindowsOnlyFragment).And.Contain(ReadmePointerFragment);
    }

    [Fact]
    public void NotSupportedEventLogWriter_ImplementsIEventLogWriter()
    {
        typeof(IEventLogWriter).IsAssignableFrom(typeof(NotSupportedEventLogWriter))
            .Should().BeTrue("NotSupportedEventLogWriter must be substitutable at the DI boundary in T14");
    }

    [Fact]
    public void NotSupportedEventLogWriter_IsSealed()
    {
        typeof(NotSupportedEventLogWriter).IsSealed
            .Should().BeTrue("the non-Windows stub has no extension points; T14 swaps the impl at DI-binding time");
    }

    [Fact]
    public void IEventLogWriter_ExtendsIDependency()
    {
        typeof(IDependency).IsAssignableFrom(typeof(IEventLogWriter))
            .Should().BeTrue("IEventLogWriter must be DI-discoverable via the IDependency marker");
    }

    [Fact]
    public void IEventLogWriter_HasNoWriteMethod()
    {
        var methodNames = typeof(IEventLogWriter)
            .GetMethods()
            .Select(m => m.Name)
            .ToArray();

        methodNames.Should().NotContain("Write",
            "M-5: runtime logging is exclusively via Serilog.Sinks.EventLog — IEventLogWriter is install/uninstall-only");
        methodNames.Should().BeEquivalentTo(
            new[] { nameof(IEventLogWriter.CreateSourceIfMissing), nameof(IEventLogWriter.RemoveSourceIfExists) },
            "the interface must expose ONLY the two install-time members");
    }

    [Fact]
    public void IEventLogWriter_HasNoConcreteImplementation_InCoreAssembly()
    {
        var coreAssembly = typeof(CoreModule).Assembly;
        var offenders = coreAssembly.GetTypes()
            .Where(t => typeof(IEventLogWriter).IsAssignableFrom(t)
                        && !t.IsInterface
                        && !t.IsAbstract)
            .ToArray();

        offenders.Should().BeEmpty(
            "T11 forbids any IEventLogWriter implementation in FreeBird.Core (B-5/B-6 invariant)");
    }
}
