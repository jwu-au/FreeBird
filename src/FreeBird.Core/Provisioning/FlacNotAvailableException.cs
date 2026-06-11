namespace FreeBird.Core.Provisioning;

/// <summary>
/// Thrown when flac or metaflac is required but cannot be located via resolver,
/// and either auto-install is disabled or unsuccessful, and the caller's operating mode
/// (e.g., --integrity l3) cannot degrade gracefully.
/// </summary>
public sealed class FlacNotAvailableException : Exception
{
    public FlacNotAvailableException(string message) : base(message) { }
    public FlacNotAvailableException(string message, Exception inner) : base(message, inner) { }
}
