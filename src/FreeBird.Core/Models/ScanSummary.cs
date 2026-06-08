namespace FreeBird.Core.Models;

/// <summary>
/// Aggregate summary printed at end of `fb scan`.
/// </summary>
public sealed record ScanSummary(
    int Processed,
    int Ok,
    int Skipped,
    int UnknownFormat,
    int IntegrityFailed,
    int Errors,
    TimeSpan Duration)
{
    public bool HasFailures => UnknownFormat > 0 || IntegrityFailed > 0 || Errors > 0;
}
