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
/// </list>
/// Without this collection, ubuntu CI runs parallel test classes and leaks
/// captured state across class boundaries (v3.4.1 hotfix).
/// </summary>
[CollectionDefinition("RunnerOverride")]
public sealed class RunnerOverrideCollection { }
