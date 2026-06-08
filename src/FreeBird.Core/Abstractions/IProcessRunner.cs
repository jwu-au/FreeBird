using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Abstraction over System.Diagnostics.Process for testability.
/// </summary>
public interface IProcessRunner : IDependency
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with the given arguments and returns the captured result.
    /// Throws on launch failure (e.g. file not found); returns non-zero ExitCode for runtime failures.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>Captured outcome of a subprocess invocation.</summary>
public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);
