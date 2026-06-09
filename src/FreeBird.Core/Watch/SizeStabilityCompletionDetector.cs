using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Watch;

/// <summary>
/// Stat-based <see cref="ICompletionDetector"/>. Tracks per-file
/// (size, mtime, consecutive-unchanged-count) and returns true on
/// <see cref="IsStableAsync"/> only when the same (size, mtime) tuple
/// has been observed <c>stabilityChecks</c> times in a row.
/// Does not sleep — caller controls the polling cadence.
/// </summary>
public sealed class SizeStabilityCompletionDetector : ICompletionDetector
{
    private readonly ConcurrentDictionary<string, Observation> _observations =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> IsStableAsync(string filePath, int stabilityChecks, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stabilityChecks);
        cancellationToken.ThrowIfCancellationRequested();

        long size;
        DateTime mtime;
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                _observations.TryRemove(filePath, out _);
                return Task.FromResult(false);
            }
            size = info.Length;
            mtime = info.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            _observations.TryRemove(filePath, out _);
            return Task.FromResult(false);
        }
        catch (UnauthorizedAccessException)
        {
            _observations.TryRemove(filePath, out _);
            return Task.FromResult(false);
        }

        var fresh = new Observation(size, mtime, 1);
        var updated = _observations.AddOrUpdate(
            filePath,
            addValueFactory: _ => fresh,
            updateValueFactory: (_, prev) =>
                (prev.Size == size && prev.MtimeUtc == mtime)
                    ? prev with { Count = prev.Count + 1 }
                    : fresh);

        return Task.FromResult(updated.Count >= stabilityChecks);
    }

    public void Forget(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _observations.TryRemove(filePath, out _);
    }

    private sealed record Observation(long Size, DateTime MtimeUtc, int Count);
}
