namespace FreeBird.Core.Models;

/// <summary>
/// Integrity check strictness configured by --integrity CLI flag.
/// </summary>
public enum IntegrityLevel
{
    /// <summary>Skip integrity check entirely (decrypt only).</summary>
    Off = 0,

    /// <summary>Structural check via TagLib# (open + duration > 0).</summary>
    L1 = 1,

    /// <summary>Full PCM-MD5 verification via `flac -t` subprocess. FLAC only; MP3/M4A fall back to L1.</summary>
    L3 = 3,

    /// <summary>Probe for `flac` binary at startup; L3 if found, else L1.</summary>
    Auto = 99,
}
