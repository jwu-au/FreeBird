using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Infrastructure;

/// <summary>
/// Writes a file atomically: write to .tmp in same directory, then rename via OS-native atomic move.
/// On any exception during write, the .tmp is cleaned up.
/// </summary>
public sealed class AtomicFileWriter : IAtomicFileWriter
{
    public async Task WriteAsync(
        string finalPath,
        Func<Stream, CancellationToken, Task> writeAction,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(writeAction);

        var directory = Path.GetDirectoryName(finalPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("finalPath must include a directory", nameof(finalPath));
        }

        Directory.CreateDirectory(directory);

        if (!overwrite && File.Exists(finalPath))
        {
            throw new IOException($"Target file already exists: {finalPath}");
        }

        var tmpPath = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                tmpPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true))
            {
                await writeAction(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tmpPath, finalPath, overwrite);
        }
        catch
        {
            TryDeleteTmp(tmpPath);
            throw;
        }
    }

    private static void TryDeleteTmp(string tmpPath)
    {
        try
        {
            if (File.Exists(tmpPath))
            {
                File.Delete(tmpPath);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
