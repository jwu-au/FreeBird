using System;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Tests.TestSupport;

/// <summary>
/// Test-only <see cref="IFileProcessorRouter"/> whose <see cref="Select"/> always returns
/// the same processor, regardless of path.
///
/// Lets the existing single-processor ScanOrchestrator / WatchOrchestrator tests — which
/// pre-date the router and stub one <see cref="IFileProcessor"/> mock — keep working with
/// a minimal change: wrap the mock in <see cref="For"/>.
/// </summary>
public sealed class SingleProcessorRouter : IFileProcessorRouter
{
    private readonly IFileProcessor _processor;

    public SingleProcessorRouter(IFileProcessor processor)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    public IFileProcessor Select(string sourcePath) => _processor;

    /// <summary>Convenience factory: a router that always routes to <paramref name="processor"/>.</summary>
    public static IFileProcessorRouter For(IFileProcessor processor) => new SingleProcessorRouter(processor);
}
