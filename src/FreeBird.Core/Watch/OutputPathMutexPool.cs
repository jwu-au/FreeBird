using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FreeBird.Core.Abstractions;

namespace FreeBird.Core.Watch;

/// <summary>
/// Per-output-path mutex pool. Prevents two concurrent <c>WatchTask</c> instances
/// (one per input directory) from writing the same output file simultaneously
/// when multiple inputs happen to resolve to the same destination filename
/// (e.g. same <c>musicId</c> appearing under two cache dirs).
///
/// <para>
/// Refcount-managed: each unique canonical path gets a <see cref="SemaphoreSlim"/>
/// (initialCount=1, maxCount=1). The pool entry is created on first acquire,
/// shared across concurrent holders, and evicted when the last holder releases.
/// </para>
///
/// <para>
/// T03 of v3.4 introduces the primitive; wiring into the file-write path lands
/// in T13. Cross-OS canonicalization (Unicode normalization, drive letters,
/// junctions, etc.) is deepened in T03b — this type implements the basic
/// case-folding matrix only.
/// </para>
/// </summary>
public interface IOutputPathMutexPool
{
    /// <summary>
    /// Acquires exclusive ownership of <paramref name="path"/>. The returned
    /// token MUST be disposed to release the mutex; double-dispose is a no-op.
    /// Pool entries are refcount-managed: created on first acquire of a key,
    /// evicted when refcount hits 0.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// Thrown if <paramref name="ct"/> is cancelled before or during the wait.
    /// In both cases the per-key refcount is decremented before rethrow so no
    /// pool entry leaks.
    /// </exception>
    ValueTask<IDisposable> AcquireAsync(string path, CancellationToken ct);

    /// <summary>
    /// For tests: current live entry count. Production code should never read this.
    /// </summary>
    int EntryCount { get; }
}

/// <summary>
/// <see cref="SemaphoreSlim"/>-backed implementation of <see cref="IOutputPathMutexPool"/>.
/// Auto-registered via the <see cref="IDependency"/> marker; the singleton lifetime
/// carve-out will be enforced explicitly in <c>CoreModule</c> (T03c).
///
/// <para>
/// We deliberately use a <c>Dictionary&lt;,&gt;</c> + <c>lock</c> rather than
/// <c>ConcurrentDictionary</c> because the refcount manipulation (TryGetValue,
/// add-if-missing, increment) must be atomic across the whole compound op —
/// <c>ConcurrentDictionary</c>'s per-bucket locking can't express this cleanly
/// without falling back to <c>AddOrUpdate</c> + a separate atomic refcount, which
/// trades simplicity for no real performance win at watch-task scale.
/// </para>
/// </summary>
public sealed class OutputPathMutexPool : IOutputPathMutexPool, IDependency, IDisposable
{
    private readonly object _poolLock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _disposed;

    public int EntryCount
    {
        get
        {
            lock (_poolLock)
            {
                return _entries.Count;
            }
        }
    }

    public async ValueTask<IDisposable> AcquireAsync(string path, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Fast-fail before incrementing refcount so a pre-cancelled token doesn't
        // create-then-evict a transient entry.
        ct.ThrowIfCancellationRequested();

        var key = Canonicalize(path);
        Entry entry;
        lock (_poolLock)
        {
            if (!_entries.TryGetValue(key, out var existing))
            {
                existing = new Entry(new SemaphoreSlim(1, 1));
                _entries[key] = existing;
            }
            existing.RefCount++;
            entry = existing;
        }

        try
        {
            // KEY anti-deadlock: WaitAsync is awaited OUTSIDE the pool lock so a
            // long-held mutex doesn't block other paths from creating their entries.
            await entry.Sem.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation (or any failure) during the wait: undo the refcount
            // bump we made above; evict the entry if we were the last waiter.
            ReleaseRefcount(key, entry, releaseSemaphore: false);
            throw;
        }

        return new Token(this, key, entry);
    }

    /// <summary>
    /// Canonicalizes <paramref name="path"/> for use as a pool key:
    /// <list type="bullet">
    ///   <item><see cref="Path.GetFullPath(string)"/> resolves <c>.</c>/<c>..</c>
    ///   and applies the process working directory to relative paths.</item>
    ///   <item>Trailing directory separators are trimmed (but the filesystem
    ///   root — e.g. <c>/</c> on Unix, <c>C:\</c> on Windows — is preserved)
    ///   because <c>GetFullPath</c> on .NET 10 does NOT collapse them.</item>
    ///   <item>On case-insensitive filesystems (Windows always; macOS APFS/HFS+
    ///   by default) the result is upper-cased so case-variant inputs map to
    ///   the same key. Linux ext4 is case-sensitive so we never fold case
    ///   there.</item>
    /// </list>
    /// </summary>
    private static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);

        // Strip trailing dir separators unless that would empty the string or
        // strip the root (e.g. "/" -> "", "C:\\" -> "C:").
        if (full.Length > 1)
        {
            var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Keep the root intact: if trimming would remove everything (e.g. "//"),
            // or leave just a drive letter without a separator on Windows ("C:"),
            // fall back to the untrimmed form.
            if (trimmed.Length > 0 && !(OperatingSystem.IsWindows() && trimmed.Length == 2 && trimmed[1] == ':'))
            {
                full = trimmed;
            }
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            full = full.ToUpperInvariant();
        }
        return full;
    }

    /// <summary>
    /// Decrements the refcount for <paramref name="key"/> and evicts the entry
    /// (disposing the underlying semaphore) when it hits zero. If
    /// <paramref name="releaseSemaphore"/> is true, also calls
    /// <see cref="SemaphoreSlim.Release()"/> before the decrement — used by the
    /// token's normal Dispose path. The cancellation cleanup path passes false
    /// because it never successfully acquired the semaphore.
    /// </summary>
    private void ReleaseRefcount(string key, Entry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Sem.Release();
        }

        lock (_poolLock)
        {
            entry.RefCount--;
            if (entry.RefCount == 0)
            {
                _entries.Remove(key);
                entry.Sem.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Pool shutdown: dispose any semaphores still in the dictionary. Production
        // callers should release all tokens before pool disposal; this is a safety
        // net to avoid leaking semaphores in test teardown paths.
        lock (_poolLock)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Sem.Dispose();
            }
            _entries.Clear();
        }
    }

    /// <summary>
    /// Mutable refcount + the per-key semaphore. Kept as a class (not a struct)
    /// so the same instance can be observed from multiple holders without
    /// boxing or copy-on-read hazards.
    /// </summary>
    private sealed class Entry
    {
        public SemaphoreSlim Sem { get; }
        public int RefCount;

        public Entry(SemaphoreSlim sem)
        {
            Sem = sem;
        }
    }

    /// <summary>
    /// Disposable handle returned to the caller. <see cref="Dispose"/> releases
    /// the semaphore and decrements the pool's refcount exactly once; subsequent
    /// disposes are no-ops (so try/using patterns are always safe).
    /// </summary>
    private sealed class Token : IDisposable
    {
        private OutputPathMutexPool? _pool;
        private readonly string _key;
        private readonly Entry _entry;

        public Token(OutputPathMutexPool pool, string key, Entry entry)
        {
            _pool = pool;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            var pool = Interlocked.Exchange(ref _pool, null);
            if (pool is null)
            {
                return;
            }
            pool.ReleaseRefcount(_key, _entry, releaseSemaphore: true);
        }
    }
}
