using Autofac;
using FluentAssertions;
using FreeBird.Core.DependencyInjection;
using FreeBird.Core.Watch;
using Serilog;

namespace FreeBird.Core.Tests.DependencyInjection;

/// <summary>
/// Verifies that the three v3.4 limiter primitives are explicitly registered as
/// <c>SingleInstance</c> so they retain process-wide semantics across child
/// lifetime scopes. The IDependency auto-scan defaults to
/// InstancePerLifetimeScope, which would silently break the contracts these
/// primitives enforce:
/// <list type="bullet">
///   <item><see cref="GlobalApiRateLimiter"/> caps in-flight API calls; a
///   per-scope instance would cap them per-scope, multiplying the real cap.</item>
///   <item><see cref="TokenBucketRateLimiter"/> regulates request spacing; a
///   per-scope bucket would let each scope spend a full burst independently.</item>
///   <item><see cref="OutputPathMutexPool"/> serialises writes to the same
///   output path; a per-scope pool would let two scopes both "own" the same
///   path concurrently, defeating the purpose.</item>
/// </list>
/// </summary>
public class CoreModuleSingletonTests
{
    private static IContainer BuildContainer()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<CoreModule>();
        var logger = new LoggerConfiguration().CreateLogger();
        builder.RegisterInstance<ILogger>(logger).SingleInstance();
        return builder.Build();
    }

    [Fact]
    public void GlobalApiRateLimiter_ResolvesAs_Singleton()
    {
        using var container = BuildContainer();
        using var scopeA = container.BeginLifetimeScope();
        using var scopeB = container.BeginLifetimeScope();

        var fromA = scopeA.Resolve<IGlobalApiRateLimiter>();
        var fromB = scopeB.Resolve<IGlobalApiRateLimiter>();
        var fromRoot = container.Resolve<IGlobalApiRateLimiter>();

        ReferenceEquals(fromA, fromB).Should().BeTrue(
            "concurrency cap must be process-wide, not per-scope");
        ReferenceEquals(fromA, fromRoot).Should().BeTrue(
            "child scope and root scope must share the singleton");
    }

    [Fact]
    public void TokenBucketRateLimiter_ResolvesAs_Singleton()
    {
        using var container = BuildContainer();
        using var scopeA = container.BeginLifetimeScope();
        using var scopeB = container.BeginLifetimeScope();

        var fromA = scopeA.Resolve<ITokenBucketRateLimiter>();
        var fromB = scopeB.Resolve<ITokenBucketRateLimiter>();
        var fromRoot = container.Resolve<ITokenBucketRateLimiter>();

        ReferenceEquals(fromA, fromB).Should().BeTrue(
            "token bucket pacing must be process-wide, not per-scope");
        ReferenceEquals(fromA, fromRoot).Should().BeTrue(
            "child scope and root scope must share the singleton");
    }

    [Fact]
    public void OutputPathMutexPool_ResolvesAs_Singleton()
    {
        using var container = BuildContainer();
        using var scopeA = container.BeginLifetimeScope();
        using var scopeB = container.BeginLifetimeScope();

        var fromA = scopeA.Resolve<IOutputPathMutexPool>();
        var fromB = scopeB.Resolve<IOutputPathMutexPool>();
        var fromRoot = container.Resolve<IOutputPathMutexPool>();

        ReferenceEquals(fromA, fromB).Should().BeTrue(
            "output-path mutex pool must be process-wide, not per-scope");
        ReferenceEquals(fromA, fromRoot).Should().BeTrue(
            "child scope and root scope must share the singleton");
    }
}
