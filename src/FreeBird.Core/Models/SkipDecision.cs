namespace FreeBird.Core.Models;

/// <summary>
/// Why a watch-cycle decided to skip a file.
/// </summary>
public enum SkipReason
{
    /// <summary>The successful output file already exists in the output directory.</summary>
    AlreadyDecoded = 0,

    /// <summary>A sidecar exists and the source file's size+mtime match what the sidecar recorded.</summary>
    SourceUnchangedSinceFailure = 1,

    /// <summary>Source file is smaller than <c>WatchOptions.MinFileSizeBytes</c>.</summary>
    SourceTooSmall = 2,

    /// <summary>Completion detector reports the file is still being written.</summary>
    NotYetStable = 3,

    /// <summary>Output exists and the per-source-stem resolution marker confirms its provenance.</summary>
    AlreadyDecodedViaMarker = 4,
}

/// <summary>
/// Outcome of <c>ISkipDecider.Decide</c>: process the file this cycle, or skip with a reason.
/// </summary>
public sealed record SkipDecision(bool ShouldProcess, SkipReason? Reason, string? Detail)
{
    public static SkipDecision Process() => new(true, null, null);

    public static SkipDecision Skip(SkipReason reason, string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return new SkipDecision(false, reason, detail);
    }
}
