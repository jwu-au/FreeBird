namespace FreeBird.Core.Metadata;

/// <summary>
/// Outcome of writing tags to a decoded music file. Sealed type hierarchy used as a
/// discriminated union — pattern-match exhaustively in callers.
/// </summary>
/// <remarks>
/// The private parameterless constructor combined with sealed nested record variants
/// guarantees the union is closed: external assemblies cannot add new cases. This
/// invariant is enforced at compile time, not at runtime, so there is no dedicated
/// unit test for it.
/// </remarks>
public abstract record TagWriteResult
{
    private TagWriteResult() { }

    /// <summary>Tags written successfully.</summary>
    public sealed record Success : TagWriteResult
    {
        /// <summary>Singleton instance — no per-call state.</summary>
        public static readonly Success Instance = new();
    }

    /// <summary>
    /// Tag write failed. <paramref name="SidecarReason"/> becomes the <c>reason</c>
    /// field in the <c>.freebird-failed</c> sidecar entry.
    /// </summary>
    /// <param name="SidecarReason">Short machine-readable reason code (e.g. "tag-io-error", "tag-tool-missing").</param>
    public sealed record Failed(string SidecarReason) : TagWriteResult;

    /// <summary>Tag writing was disabled (<c>--write-tags</c> not set). No-op result.</summary>
    public sealed record Skipped : TagWriteResult
    {
        /// <summary>Singleton instance — no per-call state.</summary>
        public static readonly Skipped Instance = new();
    }
}
