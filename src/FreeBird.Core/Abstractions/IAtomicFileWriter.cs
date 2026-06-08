using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FreeBird.Core.Abstractions;

/// <summary>
/// Writes a file atomically: write to .tmp in same directory, then rename.
/// On any exception, the .tmp is cleaned up.
/// </summary>
public interface IAtomicFileWriter : IDependency
{
    /// <summary>
    /// Writes via the given <paramref name="writeAction"/> to a temp file, then atomically moves to <paramref name="finalPath"/>.
    /// If <paramref name="overwrite"/> is false and final exists, throws IOException.
    /// </summary>
    Task WriteAsync(
        string finalPath,
        Func<Stream, CancellationToken, Task> writeAction,
        bool overwrite,
        CancellationToken cancellationToken = default);
}
