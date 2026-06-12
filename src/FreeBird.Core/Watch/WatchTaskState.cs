namespace FreeBird.Core.Watch;

/// <summary>
/// Lifecycle states of a watch task within a multi-input fb watch process.
/// </summary>
public enum WatchTaskState
{
    /// <summary>Validating directory + constructing wrapped WatchOrchestrator. Transient.</summary>
    Initializing,

    /// <summary>Wrapped WatchOrchestrator.RunAsync is executing its polling loop, processing files normally.</summary>
    Active,

    /// <summary>Entered after 3 crashes within a 60s sliding window, or directory vanished, or born-DEAD at startup.</summary>
    Dead,

    /// <summary>Probe found the directory; constructing a fresh wrapped WatchOrchestrator. Transitions to Active on success.</summary>
    Resurrecting,
}
