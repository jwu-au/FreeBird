namespace FreeBird.Core.Models;

/// <summary>
/// Configuration for a scan run, derived from CLI flags.
/// </summary>
public sealed record ScanOptions(
    string InputDirectory,
    string OutputDirectory,
    IntegrityLevel Integrity = IntegrityLevel.Auto,
    int Concurrency = 2,
    CollisionPolicy OnCollision = CollisionPolicy.Skip);

/// <summary>
/// Behavior when the target output filename already exists from a different source.
/// </summary>
public enum CollisionPolicy
{
    Skip = 0,
    Overwrite = 1,
}
