using System;

namespace FreeBird.Core.Metadata;

/// <summary>
/// Thrown by <c>INcmDecoder</c> when a <c>.ncm</c> file cannot be decoded
/// (bad magic / corrupt header / AES failure / truncated body).
/// </summary>
/// <remarks>
/// <see cref="Reason"/> is a short, human-readable string that the watch/scan
/// pipeline later writes verbatim into the quarantine sidecar's <c>reason</c> field.
/// There is intentionally no <c>error_class</c> field — the sidecar contract has none.
/// </remarks>
public sealed class NcmDecodeException : Exception
{
    /// <summary>Short human-readable failure reason; copied into the sidecar's <c>reason</c> field.</summary>
    public string Reason { get; }

    public NcmDecodeException(string reason, Exception? inner = null) : base(reason, inner)
    {
        Reason = reason;
    }
}
