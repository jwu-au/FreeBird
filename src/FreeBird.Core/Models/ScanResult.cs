namespace FreeBird.Core.Models;

/// <summary>
/// Result of processing one source file.
/// </summary>
public sealed record ScanResult(
    string SourcePath,
    ScanOutcome Outcome,
    AudioFormat Format = AudioFormat.Unknown,
    string? OutputPath = null,
    IntegrityResult? Integrity = null,
    string? Reason = null);
