using System;
using System.IO.Abstractions;

namespace FreeBird.Core.Provisioning;

/// <summary>
/// Probes the PATH environment variable for an executable. Abstracted so tests
/// can mock the lookup independently of the host OS or actual PATH contents.
/// </summary>
/// <remarks>
/// Intentionally NOT marked <see cref="FreeBird.Core.Abstractions.IDependency"/> —
/// the production <see cref="PathEnvironment"/> impl is wired by hand in the
/// Autofac module (T15) so that test seams can inject mocks without colliding
/// with assembly-scan registration.
/// </remarks>
public interface IPathEnvironment
{
    /// <summary>
    /// Returns the full path to the first directory on PATH containing
    /// <paramref name="exeName"/>, or null if not found.
    /// </summary>
    /// <param name="exeName">Executable file name (caller is responsible for any
    /// platform-specific suffix such as <c>.exe</c>).</param>
    string? FindOnPath(string exeName);
}

/// <summary>
/// Default <see cref="IPathEnvironment"/> implementation backed by
/// <see cref="Environment.GetEnvironmentVariable(string)"/> and an injectable
/// <see cref="IFileSystem"/>.
/// </summary>
public sealed class PathEnvironment : IPathEnvironment
{
    private readonly IFileSystem _fs;

    public PathEnvironment(IFileSystem fs)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
    }

    public string? FindOnPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathVar.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = _fs.Path.Combine(dir, exeName);
            if (_fs.File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
