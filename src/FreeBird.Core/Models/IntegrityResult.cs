namespace FreeBird.Core.Models;

/// <summary>
/// Outcome of an integrity check performed on a decrypted file.
/// </summary>
public sealed record IntegrityResult(
    bool Ok,
    IntegrityLevel LevelApplied,
    string? Reason = null)
{
    public static IntegrityResult Passed(IntegrityLevel level) => new(true, level);
    public static IntegrityResult Failed(IntegrityLevel level, string reason) => new(false, level, reason);
    public static IntegrityResult Skipped() => new(true, IntegrityLevel.Off);
}
