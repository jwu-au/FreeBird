namespace FreeBird.Core.Provisioning;

/// <summary>
/// Where the resolved flac/metaflac binary came from. Used for logging and diagnostics.
/// </summary>
public enum FlacBinaryProvenance
{
    /// <summary>Not resolved — binary unavailable.</summary>
    NotFound = 0,

    /// <summary>From --flac-bin CLI flag override.</summary>
    CliOverride = 1,

    /// <summary>From <fb>/flac{.exe} next to fb executable (bundled or auto-downloaded).</summary>
    NextToExecutable = 2,

    /// <summary>From PATH lookup (system-installed via brew/apt/etc).</summary>
    Path = 3,
}
