namespace FreeBird.Cli.Service;

/// <summary>
/// Cross-platform projection of the Windows Service Control Manager (SCM) state
/// reported by <see cref="IServiceController.Query(string)"/>.
///
/// The enum intentionally avoids Windows-only types (e.g. <c>ServiceControllerStatus</c>)
/// so that the surface compiles on macOS / Linux for non-Windows CLI builds.
/// </summary>
public enum ServiceState
{
    /// <summary>The service is not registered with the SCM.</summary>
    NotInstalled,

    /// <summary>The service is registered but not running.</summary>
    Stopped,

    /// <summary>The service has been asked to start and is transitioning.</summary>
    StartPending,

    /// <summary>The service is running.</summary>
    Running,

    /// <summary>The service has been asked to stop and is transitioning.</summary>
    StopPending,

    /// <summary>The service is paused (rare for FreeBird, but mapped for completeness).</summary>
    Paused,

    /// <summary>An SCM state that does not map to any of the above (e.g. ContinuePending, PausePending).</summary>
    Other,
}
