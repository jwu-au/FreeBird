using System;
using System.IO;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Processing;

/// <summary>
/// Default <see cref="IFileProcessorRouter"/>: a trivial extension switch.
///
/// Holds BOTH concrete processors. They are injected by concrete type (not via
/// <see cref="IFileProcessor"/>) because only ONE of them is the DI <see cref="IFileProcessor"/>
/// binding (<see cref="FileProcessor"/>); both are registered <c>AsSelf()</c> in CoreModule
/// so the router can resolve each unambiguously.
///
/// ponytail: this is a two-way if/else on a single extension. If a third encrypted
/// container format ever appears, replace the if with a small map keyed by extension
/// (or a list of (predicate, processor) pairs) — but two formats don't justify that yet.
/// </summary>
public sealed class FileProcessorRouter : IFileProcessorRouter
{
    private readonly FileProcessor _ucProcessor;
    private readonly NcmFileProcessor _ncmProcessor;

    public FileProcessorRouter(FileProcessor ucProcessor, NcmFileProcessor ncmProcessor)
    {
        _ucProcessor = ucProcessor ?? throw new ArgumentNullException(nameof(ucProcessor));
        _ncmProcessor = ncmProcessor ?? throw new ArgumentNullException(nameof(ncmProcessor));
    }

    public IFileProcessor Select(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (Path.GetExtension(sourcePath).Equals(".ncm", StringComparison.OrdinalIgnoreCase))
        {
            return _ncmProcessor;
        }
        return _ucProcessor;
    }
}
