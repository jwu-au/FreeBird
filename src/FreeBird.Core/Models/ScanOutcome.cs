namespace FreeBird.Core.Models;

/// <summary>
/// Final outcome of processing a single .uc / .uc! file.
/// </summary>
public enum ScanOutcome
{
    /// <summary>Decoded + integrity verified + written to output dir.</summary>
    Ok = 0,

    /// <summary>Output filename collision; existing file kept (skip policy).</summary>
    Skipped = 1,

    /// <summary>Decryption succeeded but format not recognized; file in quarantine.</summary>
    UnknownFormat = 2,

    /// <summary>Integrity check failed; file in quarantine with sidecar reason.</summary>
    IntegrityFailed = 3,

    /// <summary>Unrecoverable error (IO, permission, etc).</summary>
    Error = 4,
}
