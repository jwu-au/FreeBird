using System.Collections.Generic;

namespace FreeBird.Core.NetEase;

/// <summary>
/// Single source of truth for the NetEase API body <c>code</c> values FreeBird
/// classifies on. NetEase signals rate-limit / risk-control as HTTP 200 with a
/// nonzero body code (notably -460 "Cheating" and -447), which must be treated
/// as throttling rather than a genuine not-found.
/// </summary>
internal static class NetEaseApiCodes
{
    /// <summary>The body <c>code</c> for a successful song-detail response.</summary>
    public const int Success = 200;

    /// <summary>
    /// Body <c>code</c> values that indicate risk-control / rate-limiting on an
    /// HTTP-200 response (-460 "Cheating", -447). Shared by the client and tests.
    /// </summary>
    public static readonly IReadOnlySet<int> RiskControl = new HashSet<int> { -460, -447 };
}
