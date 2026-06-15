using Xunit;

namespace FreeBird.Cli.Tests;

/// <summary>
/// xUnit collection that serialises test classes which mutate process-wide
/// static override fields used by the CLI test harness:
/// <list type="bullet">
///   <item><c>ScanRunner.RunnerOverride</c></item>
///   <item><c>WatchCommand.HandlerOverride</c></item>
///   <item><c>WatchRunner.OrchestratorFactoryOverride</c></item>
///   <item><c>WatchRunner.CoordinatorFactoryOverride</c></item>
///   <item><c>InstallFlacRunner.ContainerOverride</c></item>
/// </list>
/// Without this collection, ubuntu and Windows CI run parallel test classes
/// and leak captured state across class boundaries.
/// </summary>
[CollectionDefinition("GlobalStaticState")]
public sealed class GlobalStaticStateCollection { }
